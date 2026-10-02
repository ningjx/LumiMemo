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
    /// 绘制布局结果。坐标系为 DIP；调用方负责缩放变换（物理像素 = DIP × 倍率）。
    /// </summary>
    public void Render(CanvasDrawingSession session, LayoutResult layout, Color textColor, bool debugOverlay)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);

        foreach (var f in layout.Floats)
        {
            var r = f.Rect;
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
        int i = 0;
        while (i < lines.Count)
        {
            // 分组：(Batch, 段 X) 相同且堆叠一致（跨段基线抬升处切新组，正确性优先）。
            int j = i + 1;
            while (j < lines.Count
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
        foreach (var line in lines)
        {
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
                    DrawCheckbox(session, line, todo, textColor);
                    break;
            }
        }
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
    /// Todo 矢量复选框（§6.2/O2）：首行行盒左侧缩进区内、垂直居中于行盒；
    /// 1.5px 描边 2px 圆角，已勾选时两条线段画对勾；颜色随主题墨色。
    /// </summary>
    private static void DrawCheckbox(
        CanvasDrawingSession session, PlacedLine line, TodoBlock todo, Color ink)
    {
        const float boxSize = 20f;
        float x = line.X - todo.LeftIndent;
        float y = line.Y + (line.Height - boxSize) / 2f;
        var rect = new Windows.Foundation.Rect(x, y, boxSize, boxSize);
        session.DrawRoundedRectangle(rect, 2, 2, ink, 1.5f);
        if (todo.Checked)
        {
            session.DrawLine(x + 4.5f, y + 10.5f, x + 8.5f, y + 14.5f, ink, 2f);
            session.DrawLine(x + 8.5f, y + 14.5f, x + 15.5f, y + 5.5f, ink, 2f);
        }
    }
}
