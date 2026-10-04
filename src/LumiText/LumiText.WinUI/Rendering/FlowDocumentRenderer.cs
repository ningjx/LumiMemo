using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Layout;
using Windows.UI;

namespace LumiText.WinUI.Rendering;

/// <summary>
/// 把 <see cref="LayoutResult"/> 绘制到 <see cref="CanvasDrawingSession"/>。
/// 持有当前布局结果并负责其释放；记录最近一次排版耗时与批量统计（验收指标用）。
/// </summary>
/// <remarks>
/// M3 按批分组绘制（Phase 1 设计 §7.3）：同一批的多个行盒共享一个 CanvasTextLayout，
/// 逐行盒调 DrawTextLayout 会把整批光栅化 N 遍（O(行数²)）。分组规则：
/// 按 (Batch, 段 X) 分组，组内逐行校验「引擎放置与批内堆叠一致」
/// （前行 Y + Height ≈ 本行 Y）→ 一致并入，不一致切新组（跨段基线抬升处宁可多画一组）；
/// 每组一次 DrawTextLayout，原点 =（组首行 X, 组首行 Y − 组首行 LineOffsetY），
/// 裁剪到组覆盖的行盒 Y 区间（批内未提交行落在裁剪区外，不会误显）。
/// </remarks>
public sealed class FlowDocumentRenderer
{
    private static readonly Color FloatFill = Color.FromArgb(70, 122, 90, 220);
    private static readonly Color FloatStroke = Color.FromArgb(190, 122, 90, 220);
    private static readonly Color DebugLineBox = Color.FromArgb(90, 220, 80, 80);
    private static readonly Color DividerLine = Color.FromArgb(90, 40, 32, 48);

    /// <summary>堆叠一致性容差（与引擎 Epsilon 同值）。</summary>
    private const float StackingEpsilon = 0.01f;

    /// <summary>Todo 复选框悬停高亮底色（Phase 3 M3，墨色低 alpha）。</summary>
    private static readonly Color CheckboxHoverFill = Color.FromArgb(0x28, 40, 32, 48);

    /// <summary>Todo 复选框边长 = 行高 × 这个比例，上限 20dip。★ 随手感调。</summary>
    private const float BoxHeightRatio = 0.7f;

    /// <summary>已勾选的待办：正文压暗到原 alpha 的这个比例（"做完了"的观感）。★ 随手感调。</summary>
    private const float CheckedTodoInkOpacity = 0.6f;

    /// <summary>悬停中的 todo 块索引（-1 = 无）：方框外圈画高亮底（Phase 3 M3）。</summary>
    public int HoverTodoBlockIndex { get; set; } = -1;

    /// <summary>正在播放勾选动画的 todo 块索引（-1 = 无）；进度见 <see cref="AnimatedTodoProgress"/>。</summary>
    public int AnimatedTodoBlockIndex { get; set; } = -1;

    /// <summary>勾选动画进度 0–1（仅对 <see cref="AnimatedTodoBlockIndex"/> 生效）。</summary>
    public float AnimatedTodoProgress { get; set; }

    private static Color FromColor32(Color32 color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    private readonly FlowLayoutEngine _engine;

    public FlowDocumentRenderer(ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        _engine = new FlowLayoutEngine(measurer);
    }

    /// <summary>当前生效的布局结果（可能为 <see langword="null"/>，尚未排版时）。</summary>
    public LayoutResult? Current { get; private set; }

    /// <summary>
    /// 文档图片缓存（§8.1/8.2）：设置后，渲染把浮动 ImageBlock 画为真实位图
    /// （未就绪/解码失败时保留调试占位矩形，位图就绪由宿主 Invalidate 补画）。
    /// </summary>
    public DocumentImageStore? Images { get; set; }

    /// <summary>最近一次全量排版耗时。</summary>
    public TimeSpan LastLayoutDuration { get; private set; }

    /// <summary>最近一次排版的批量统计（建批/弃批数，§10.3 基准数据源）。</summary>
    public LayoutStats LastLayoutStats => _engine.LastStats;

    /// <summary>全量重排并替换当前结果（旧结果随之释放）。</summary>
    public LayoutResult UpdateLayout(
        IReadOnlyList<Block> blocks,
        IReadOnlyList<FloatObject> floats,
        float contentWidth)
    {
        var watch = Stopwatch.StartNew();
        var result = _engine.Layout(blocks, floats, contentWidth);
        watch.Stop();
        Current?.Dispose();
        Current = result;
        LastLayoutDuration = watch.Elapsed;
        return result;
    }

    /// <summary>全量重排（文档模型入口：块列表 + 由 ImageBlock 派生的浮动输入）。</summary>
    public LayoutResult UpdateLayout(Document document, float contentWidth)
    {
        ArgumentNullException.ThrowIfNull(document);
        return UpdateLayout(document.Blocks, document.GetFloats(), contentWidth);
    }

    /// <summary>
    /// 按给定浮动集合排版一次、但不接管为当前结果，返回值由调用方释放
    /// （Phase 3 M4：拖动改锚点时用「去掉该图片」的自然版面判定锚点——
    /// 预览版面里文字已被图片挤开，直接命中会落到图片左侧多一个字）。
    /// </summary>
    public LayoutResult LayoutTransient(IReadOnlyList<Block> blocks,
        IReadOnlyList<FloatObject> floats, float contentWidth)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(floats);
        return _engine.Layout(blocks, floats, contentWidth);
    }

    /// <summary>
    /// 绘制布局结果（整文档口径，等价 viewport = 全文档）。
    /// 坐标系为 DIP；调用方负责缩放变换（物理像素 = DIP × 倍率）。
    /// </summary>
    public void Render(CanvasDrawingSession session, LayoutResult layout, Color textColor, bool debugOverlay) =>
        Render(session, layout, new Windows.Foundation.Rect(0, 0, 1_000_000, 1_000_000), textColor, debugOverlay);

    /// <summary>
    /// 绘制布局结果与 <paramref name="viewport"/>（文档坐标 DIP）相交的部分（§7.3 视口裁剪版）：
    /// 行盒 Y 有序，二分定位首行；只画与视口相交的批组、浮动与块级覆盖层。
    /// 调用方负责把「文档坐标 → surface 局部坐标」的平移放进 session.Transform。
    /// </summary>
    /// <param name="includeTextBackgrounds">是否连带画文字底色。编辑宿主（LumiEditor）的 z 序是
    /// 「选区高亮 → 文字底色 → 浮动/文字」，需自行先调 <see cref="RenderTextBackgrounds"/>
    /// 再带 <c>false</c> 调本方法，避免底色盖住选区；其余宿主保持默认一次画全。</param>
    public void Render(
        CanvasDrawingSession session,
        LayoutResult layout,
        Windows.Foundation.Rect viewport,
        Color textColor,
        bool debugOverlay,
        bool includeTextBackgrounds = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);

        float viewTop = (float)viewport.Y;
        float viewBottom = (float)(viewport.Y + viewport.Height);

        foreach (var f in layout.Floats)
        {
            var r = f.Rect;
            if (r.Bottom < viewTop || r.Y > viewBottom)
            {
                continue;
            }
            var rect = new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height);
            // §8.2：浮动 ImageBlock 且位图就绪 → 圆角层 + DrawImage；否则画调试占位矩形
            // （未解码完成/解码失败/S2 调试浮动共用占位路径，位图就绪后由宿主 Invalidate 补画）。
            if (TryGetFloatImage(layout, f, out var bitmap))
            {
                // 画「视觉矩形」（FloatGeometry.VisualRect）：包络与视觉矩形之间的缓冲带
                // 排版上也留给了文字（Phase 3 打磨），所以这里画小一点不会与文字打架
                var v = FloatGeometry.VisualRect(r);
                var painted = new Windows.Foundation.Rect(v.X, v.Y, v.Width, v.Height);
                using (session.CreateLayer(1f,
                    CanvasGeometry.CreateRoundedRectangle(session.Device, painted,
                        FloatGeometry.CornerRadius, FloatGeometry.CornerRadius)))
                {
                    session.DrawImage(bitmap, painted,
                        new Windows.Foundation.Rect(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height));
                }
                // 图片本体不描边（圆角保留）：选中框由编辑器的覆盖层单独画
            }
            else
            {
                session.FillRoundedRectangle(rect, FloatGeometry.CornerRadius, FloatGeometry.CornerRadius, FloatFill);
                session.DrawRoundedRectangle(rect, FloatGeometry.CornerRadius, FloatGeometry.CornerRadius, FloatStroke, 1.5f);
            }
        }

        var lines = layout.Lines;

        // 文字底色（Phase 3 打磨）：浮动之后、文字之前铺——与文字同层，不盖住图片
        if (includeTextBackgrounds)
        {
            RenderTextBackgrounds(session, layout, viewport);
        }

        int i = FirstLineAt(lines, viewTop);
        while (i < lines.Count && lines[i].Y < viewBottom)
        {
            // 分组：(Batch, 段 X) 相同且堆叠一致（跨段基线抬升处切新组，正确性优先）。
            // 分组只在可见行范围内进行（§7.3）；批内不可见行落不出裁剪区，不会误显。
            int j = i + 1;
            while (j < lines.Count
                && lines[j].Y < viewBottom
                && ReferenceEquals(lines[j].Batch, lines[i].Batch)
                && lines[j].X == lines[i].X
                && Math.Abs(lines[j - 1].Y + lines[j - 1].Height - lines[j].Y) <= StackingEpsilon)
            {
                j++;
            }

            var first = lines[i];
            var last = lines[j - 1];
            if (first.Batch?.NativeLayout is CanvasTextLayout native)
            {
                // 已勾选的待办：本段文字压暗一点（"做完了"的语义）；方框与对勾保持原色
                Color groupInk = IsCheckedTodo(layout, first.BlockIndex) ? Dimmed(textColor) : textColor;
                float originY = first.Y - first.LineOffsetY;
                var clip = new Windows.Foundation.Rect(
                    first.X, first.Y, 1_000_000, last.Y + last.Height - first.Y);
                using (session.CreateLayer(1f, clip))
                {
                    session.DrawTextLayout(native, first.X, originY, groupInk);
                }
            }

            if (debugOverlay)
            {
                for (int k = i; k < j; k++)
                {
                    var b = lines[k].Bounds;
                    session.DrawRectangle(
                        new Windows.Foundation.Rect(b.X, b.Y, Math.Max(1, b.Width), b.Height),
                        DebugLineBox, 0.75f);
                }
            }
            i = j;
        }

        // 块级覆盖层（§6.2）：Todo 矢量复选框、Divider 线——与文本批分组正交，单独一遍。
        for (int k = FirstLineAt(lines, viewTop); k < lines.Count && lines[k].Y < viewBottom; k++)
        {
            var line = lines[k];
            switch (line.Kind)
            {
                case PlacedLineKind.Divider:
                {
                    float midY = line.Y + line.Height / 2f;
                    session.DrawLine(line.X, midY, line.X + line.Width, midY, DividerLine, 1f);
                    break;
                }
                case PlacedLineKind.TodoText
                    when line.IsBlockStart
                        && layout.Blocks is { } blocks
                        && line.BlockIndex < blocks.Count
                        && blocks[line.BlockIndex] is TodoBlock todo:
                    DrawCheckbox(session, line, todo, textColor,
                        hovered: line.BlockIndex == HoverTodoBlockIndex,
                        animationProgress: line.BlockIndex == AnimatedTodoBlockIndex
                            ? AnimatedTodoProgress
                            : -1f);
                    break;
                case PlacedLineKind.BulletText
                    when line.IsBlockStart:
                    DrawBullet(session, line, textColor);
                    break;
            }
        }
    }

    /// <summary>
    /// 文字底色通道（Phase 3 打磨，取代原块级背景通道）：把带
    /// <see cref="InlineStyle.Background"/> 的 run 的字符区域铺成矩形。
    /// 单独暴露是为了让编辑宿主把它排在选区高亮之后（选区 → 底色 → 文字），
    /// 否则选中的带底色文字看不到选区色。
    /// </summary>
    public void RenderTextBackgrounds(
        CanvasDrawingSession session, LayoutResult layout, Windows.Foundation.Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);
        float viewBottom = (float)(viewport.Y + viewport.Height);
        var lines = layout.Lines;
        for (int k = FirstLineAt(lines, (float)viewport.Y); k < lines.Count && lines[k].Y < viewBottom; k++)
        {
            DrawRunBackgrounds(session, layout, lines[k]);
        }
    }

    /// <summary>
    /// 单行的文字底色：本行里带 <see cref="InlineStyle.Background"/> 的 run 的字符区域，
    /// 批坐标 → 文档坐标的换算与选区几何同一条（<see cref="PlacedLine.BatchStart"/> /
    /// <see cref="PlacedLine.LineOffsetY"/>）。
    /// </summary>
    private static void DrawRunBackgrounds(
        CanvasDrawingSession session, LayoutResult layout, PlacedLine line)
    {
        if (line.Batch is not { } batch
            || layout.Blocks is not { } blocks
            || line.BlockIndex < 0 || line.BlockIndex >= blocks.Count
            || !BlockTextOps.IsTextBlock(blocks[line.BlockIndex]))
        {
            return;
        }

        int lineEnd = line.CharStart + line.CharCount;
        int runStart = 0;
        foreach (var run in BlockTextOps.GetRuns(blocks[line.BlockIndex]))
        {
            int runEnd = runStart + run.Text.Length;
            int start = Math.Max(runStart, line.CharStart);
            int end = Math.Min(runEnd, lineEnd);
            runStart = runEnd;
            if (start >= end || run.Style?.Background is not { } background)
            {
                continue;
            }
            foreach (var region in batch.GetCharRegions(start - line.BatchStart, end - start))
            {
                session.FillRectangle(
                    new Windows.Foundation.Rect(
                        line.X + region.X,
                        line.Y + (region.Y - line.LineOffsetY),
                        region.Width,
                        region.Height),
                    FromColor32(background));
            }
        }
    }

    /// <summary>行盒按 Y 有序：二分找第一行「底缘 ≥ viewTop」的行。</summary>
    private static int FirstLineAt(IReadOnlyList<PlacedLine> lines, float viewTop)
    {
        int lo = 0;
        int hi = lines.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Y + lines[mid].Height < viewTop)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// 浮动块对应的图片位图（§8.2）：FloatObject.Id = 源 ImageBlock 的块索引
    /// （<see cref="Document.GetFloats"/> 派生约定），经 <see cref="Images"/> 查解码结果。
    /// </summary>
    private bool TryGetFloatImage(LayoutResult layout, FloatObject f, out CanvasBitmap bitmap)
    {
        bitmap = null!;
        if (Images is null
            || layout.Blocks is not { } blocks
            || f.Id < 0 || f.Id >= blocks.Count
            || blocks[f.Id] is not ImageBlock image)
        {
            return false;
        }
        var found = Images.TryGet(image.ImageId);
        if (found is null)
        {
            return false;
        }
        bitmap = found;
        return true;
    }

    /// <summary>该块是不是"已勾选的待办"（渲染层据此压暗正文）。</summary>
    private static bool IsCheckedTodo(LayoutResult layout, int blockIndex) =>
        layout.Blocks is { } blocks && blockIndex >= 0 && blockIndex < blocks.Count
        && blocks[blockIndex] is TodoBlock { Checked: true };

    /// <summary>压暗：按 <see cref="CheckedTodoInkOpacity"/> 降 alpha——在玻璃上叠出来的就是"灰一点"，
    /// 比写死一个灰值稳（深色主题那轮也不用改这里）。</summary>
    private static Color Dimmed(Color color) =>
        Color.FromArgb((byte)Math.Round(color.A * CheckedTodoInkOpacity), color.R, color.G, color.B);

    /// <summary>对勾的描边样式：圆头 + 圆角接头（方头在小方框里看着生硬）。</summary>
    private static readonly CanvasStrokeStyle CheckStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    /// <summary>
    /// Todo 矢量复选框（§6.2/O2 + Phase 3 M3）：首行行盒左侧缩进区内、垂直居中于行盒；
    /// 2px 描边、圆角给足（内圈也要看得出圆），已勾选时画圆头对勾；颜色随主题墨色。
    /// M3 追加：悬停时方框外圈画高亮底；勾选动画按进度（0–1）从起点描出对勾，
    /// 方框随之轻微缩放（1 → 1.06 → 1）；<paramref name="animationProgress"/> &lt; 0 = 不播动画。
    /// </summary>
    /// <remarks>
    /// 边长**随行高走**（行高的 <c>BoxHeightRatio</c>，上限 20）：正文行高只有 20 出头，
    /// 写死 20 的框会比行还高，上下相邻的待办框就贴在一起了（2026-10-04 实机截图）。
    /// 对勾三点按框宽高取比例，换尺寸不用重算；横向上框在缩进区里居中，不侵进正文。
    /// 圆角取 <c>0.25 × 边长</c>：描边是**居中**画的，半径太小的话外圈看着圆、内圈还是尖的。
    /// </remarks>
    private static void DrawCheckbox(CanvasDrawingSession session, PlacedLine line, TodoBlock todo,
        Color ink, bool hovered, float animationProgress)
    {
        float boxSize = MathF.Min(20f, line.Height * BoxHeightRatio);
        float centerX = line.X - (todo.LeftIndent / 2f);
        float centerY = line.Y + (line.Height / 2f);

        float scale = animationProgress < 0f
            ? 1f
            : 1f + 0.06f * MathF.Sin(MathF.PI * animationProgress);
        float half = boxSize / 2f * scale;
        var rect = new Windows.Foundation.Rect(centerX - half, centerY - half, half * 2f, half * 2f);
        float radius = boxSize * 0.25f;

        if (hovered)
        {
            // 悬停高亮：画在方框之下，不遮描边
            float pad = boxSize * 0.15f;
            session.FillRoundedRectangle(
                new Windows.Foundation.Rect(rect.X - pad, rect.Y - pad, rect.Width + (pad * 2f), rect.Height + (pad * 2f)),
                radius * 1.6f, radius * 1.6f, CheckboxHoverFill);
        }

        session.DrawRoundedRectangle(rect, radius, radius, ink, 2f);
        if (!todo.Checked)
        {
            return;
        }

        // 对勾三点：按框宽高的固定比例取（原先是 20px 框的绝对坐标，换尺寸就得重算）
        float left = (float)rect.X;
        float top = (float)rect.Y;
        float x0 = left + (0.225f * (float)rect.Width), y0 = top + (0.525f * (float)rect.Height);
        float x1 = left + (0.425f * (float)rect.Width), y1 = top + (0.725f * (float)rect.Height);
        float x2 = left + (0.775f * (float)rect.Width), y2 = top + (0.275f * (float)rect.Height);

        if (animationProgress < 0f)
        {
            session.DrawLine(x0, y0, x1, y1, ink, 2f, CheckStroke);
            session.DrawLine(x1, y1, x2, y2, ink, 2f, CheckStroke);
            return;
        }

        // 按进度描出：总长 = 两段之和，先画短段、超出部分画长段
        float length1 = MathF.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        float length2 = MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
        float drawn = (length1 + length2) * Math.Clamp(animationProgress, 0f, 1f);
        if (drawn <= 0f)
        {
            return;
        }
        if (drawn <= length1)
        {
            float t = drawn / length1;
            session.DrawLine(x0, y0, x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), ink, 2f, CheckStroke);
            return;
        }
        session.DrawLine(x0, y0, x1, y1, ink, 2f, CheckStroke);
        float t2 = (drawn - length1) / length2;
        session.DrawLine(x1, y1, x1 + ((x2 - x1) * t2), y1 + ((y2 - y1) * t2), ink, 2f, CheckStroke);
    }

    /// <summary>
    /// Bullet 圆点（§0.1 分点基线）：首行行盒左侧缩进区内、垂直居中于行盒；
    /// 实心圆直径 5px，颜色随主题墨色。缩进 16 全由本方法消费（与 DrawCheckbox 同纪律）。
    /// </summary>
    private static void DrawBullet(CanvasDrawingSession session, PlacedLine line, Color ink)
    {
        const float diameter = 5f;
        const float indent = 16f; // 与 ParagraphBlock.LeftIndent 同步
        float cx = line.X - indent / 2f;
        float cy = line.Y + line.Height / 2f;
        session.FillCircle(cx, cy, diameter / 2f, ink);
    }
}
