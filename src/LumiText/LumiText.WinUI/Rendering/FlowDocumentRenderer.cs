using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
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

    /// <summary>块背景圆角半径（dip，Phase 3 §4；视觉值走界面检查微调）。</summary>
    private const float BackgroundCornerRadius = 5f;

    /// <summary>Todo 复选框悬停高亮底色（Phase 3 M3，墨色低 alpha）。</summary>
    private static readonly Color CheckboxHoverFill = Color.FromArgb(0x28, 40, 32, 48);

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
    /// <param name="includeBlockBackgrounds">是否连带画块背景。编辑宿主（LumiEditor）的 z 序是
    /// 「块背景 → 选区高亮 → 浮动/文字」，需自行先调 <see cref="RenderBlockBackgrounds"/>
    /// 再带 <c>false</c> 调本方法，避免背景盖住选区；其余宿主保持默认一次画全。</param>
    public void Render(
        CanvasDrawingSession session,
        LayoutResult layout,
        Windows.Foundation.Rect viewport,
        Color textColor,
        bool debugOverlay,
        bool includeBlockBackgrounds = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);

        float viewTop = (float)viewport.Y;
        float viewBottom = (float)(viewport.Y + viewport.Height);

        if (includeBlockBackgrounds)
        {
            RenderBlockBackgrounds(session, layout, viewport);
        }

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
                using (session.CreateLayer(1f,
                    CanvasGeometry.CreateRoundedRectangle(session.Device, rect, 6f, 6f)))
                {
                    session.DrawImage(bitmap, rect,
                        new Windows.Foundation.Rect(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height));
                }
                session.DrawRoundedRectangle(rect, 6, 6, FloatStroke, 1.5f);
            }
            else
            {
                session.FillRoundedRectangle(rect, 6, 6, FloatFill);
                session.DrawRoundedRectangle(rect, 6, 6, FloatStroke, 1.5f);
            }
        }

        var lines = layout.Lines;
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
                float originY = first.Y - first.LineOffsetY;
                var clip = new Windows.Foundation.Rect(
                    first.X, first.Y, 1_000_000, last.Y + last.Height - first.Y);
                using (session.CreateLayer(1f, clip))
                {
                    session.DrawTextLayout(native, first.X, originY, textColor);
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
    /// 块级背景通道（Phase 3 §4/§6）：整列圆角矩形，画在浮动与文字之下（浮动图片压在其上）。
    /// 单独暴露是为了让编辑宿主把它排在选区高亮之前（背景 → 选区 → 文字）。
    /// </summary>
    public void RenderBlockBackgrounds(
        CanvasDrawingSession session, LayoutResult layout, Windows.Foundation.Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Blocks is not { } blocks)
        {
            return;
        }

        float viewTop = (float)viewport.Y;
        float viewBottom = (float)(viewport.Y + viewport.Height);
        foreach (var extent in layout.BlockExtents)
        {
            var r = extent.Rect;
            if (r.Bottom < viewTop || r.Y > viewBottom)
            {
                continue;
            }
            if (extent.BlockIndex >= blocks.Count
                || blocks[extent.BlockIndex].Background is not { } background)
            {
                continue;
            }
            session.FillRoundedRectangle(
                new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height),
                BackgroundCornerRadius, BackgroundCornerRadius,
                FromColor32(background));
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

    /// <summary>
    /// Todo 矢量复选框（§6.2/O2 + Phase 3 M3）：首行行盒左侧缩进区内、垂直居中于行盒；
    /// 1.5px 描边 2px 圆角，已勾选时两条线段画对勾；颜色随主题墨色。
    /// M3 追加：悬停时方框外圈画高亮底；勾选动画按进度（0–1）从起点描出对勾，
    /// 方框随之轻微缩放（1 → 1.06 → 1）；<paramref name="animationProgress"/> &lt; 0 = 不播动画。
    /// </summary>
    private static void DrawCheckbox(CanvasDrawingSession session, PlacedLine line, TodoBlock todo,
        Color ink, bool hovered, float animationProgress)
    {
        const float boxSize = 20f;
        float centerX = line.X - todo.LeftIndent + boxSize / 2f;
        float centerY = line.Y + line.Height / 2f;

        float scale = animationProgress < 0f
            ? 1f
            : 1f + 0.06f * MathF.Sin(MathF.PI * animationProgress);
        float half = boxSize / 2f * scale;
        var rect = new Windows.Foundation.Rect(centerX - half, centerY - half, half * 2f, half * 2f);

        if (hovered)
        {
            // 悬停高亮：画在方框之下，不遮描边
            session.FillRoundedRectangle(
                new Windows.Foundation.Rect(rect.X - 3f, rect.Y - 3f, rect.Width + 6f, rect.Height + 6f),
                5, 5, CheckboxHoverFill);
        }

        session.DrawRoundedRectangle(rect, 2, 2, ink, 1.5f);
        if (!todo.Checked)
        {
            return;
        }

        // 对勾三点（方框左上角起的固定比例坐标，随缩放）
        float left = (float)rect.X;
        float top = (float)rect.Y;
        float x0 = left + (4.5f * scale), y0 = top + (10.5f * scale);
        float x1 = left + (8.5f * scale), y1 = top + (14.5f * scale);
        float x2 = left + (15.5f * scale), y2 = top + (5.5f * scale);

        if (animationProgress < 0f)
        {
            session.DrawLine(x0, y0, x1, y1, ink, 2f);
            session.DrawLine(x1, y1, x2, y2, ink, 2f);
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
            session.DrawLine(x0, y0, x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), ink, 2f);
            return;
        }
        session.DrawLine(x0, y0, x1, y1, ink, 2f);
        float t2 = (drawn - length1) / length2;
        session.DrawLine(x1, y1, x1 + ((x2 - x1) * t2), y1 + ((y2 - y1) * t2), ink, 2f);
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
