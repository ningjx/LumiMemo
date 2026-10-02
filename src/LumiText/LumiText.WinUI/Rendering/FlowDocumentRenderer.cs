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

    /// <summary>最近一次全量排版耗时。</summary>
    public TimeSpan LastLayoutDuration { get; private set; }

    /// <summary>最近一次排版的批量统计（建批/弃批数，§10.3 基准数据源）。</summary>
    public LayoutStats LastLayoutStats => _engine.LastStats;

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
    }
}
