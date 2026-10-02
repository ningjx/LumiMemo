using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Windows.UI;

namespace LumiText.WinUI.Rendering;

/// <summary>
/// 把 <see cref="LayoutResult"/> 绘制到 <see cref="CanvasDrawingSession"/>。
/// 持有当前布局结果并负责其释放；记录最近一次排版耗时（验收指标用）。
/// </summary>
public sealed class FlowDocumentRenderer
{
    private static readonly Color FloatFill = Color.FromArgb(70, 122, 90, 220);
    private static readonly Color FloatStroke = Color.FromArgb(190, 122, 90, 220);
    private static readonly Color DebugLineBox = Color.FromArgb(90, 220, 80, 80);

    private readonly FlowLayoutEngine _engine;

    public FlowDocumentRenderer(ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        _engine = new FlowLayoutEngine(measurer);
    }

    /// <summary>当前生效的布局结果（可能为 <see langword="null"/>，尚未排版时）。</summary>
    public LayoutResult? Current { get; private set; }

    /// <summary>最近一次全量排版耗时。</summary>
    public TimeSpan LastLayoutDuration { get; private set; }

    /// <summary>全量重排并替换当前结果（旧结果随之释放）。</summary>
    public LayoutResult UpdateLayout(
        IReadOnlyList<ParagraphBlock> paragraphs,
        IReadOnlyList<FloatObject> floats,
        float contentWidth)
    {
        var watch = Stopwatch.StartNew();
        var result = _engine.Layout(paragraphs, floats, contentWidth);
        watch.Stop();
        Current?.Dispose();
        Current = result;
        LastLayoutDuration = watch.Elapsed;
        return result;
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
            session.FillRoundedRectangle(rect, 6, 6, FloatFill);
            session.DrawRoundedRectangle(rect, 6, 6, FloatStroke, 1.5f);
        }

        foreach (var line in layout.Lines)
        {
            if (line.NativeLayout is CanvasTextLayout native)
            {
                // 随行布局包含段宽内的全部换行，裁剪到行盒高度只画第一行。
                using (session.CreateLayer(1f, new Windows.Foundation.Rect(line.X, line.Y, 1_000_000, line.Height)))
                {
                    session.DrawTextLayout(native, line.X, line.Y, textColor);
                }
            }
            if (debugOverlay)
            {
                var b = line.Bounds;
                session.DrawRectangle(
                    new Windows.Foundation.Rect(b.X, b.Y, Math.Max(1, b.Width), b.Height),
                    DebugLineBox, 0.75f);
            }
        }
    }
}
