using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-B4 系列：批所有权不变式（Phase 1 设计 §5.2 契约）——
/// ① 每个被 <see cref="PlacedLine"/> 引用的批在 <see cref="FlowLayoutEngine.Layout"/> 返回时必须存活；
/// ② 未被任何行盒引用的批（弃批、交集重探替换掉的批）在返回时必须已释放。
/// 假字体的 Dispose 是空实现，T-B1/B2/B3 对①完全无感；本测试在度量器侧记账。
/// ① 的违约在 Win2D 侧表现为渲染层 DrawTextLayout 抛 RO_E_CLOSED（"Cannot access a disposed object."）。
/// </summary>
public sealed class BatchOwnershipTests
{
    private const float W = 100f;

    private static FloatObject Left(int id, float x, float y, float w, float h) =>
        new(id, new LayoutRect(x, y, w, h), FloatSide.Left);

    /// <summary>全宽行 10 字符的假字体 + 逐批释放记账。</summary>
    private sealed class TrackingMeasurer : ITextMeasurer
    {
        private readonly FakeTextMeasurer _inner = new();

        /// <summary>本次度量器创建过的全部批，按创建顺序。</summary>
        public List<TrackingBatch> Batches { get; } = new();

        public ILineBatch LayoutLines(IReadOnlyList<TextRun> runs, TextStyle baseStyle, float maxWidth)
        {
            var batch = new TrackingBatch(_inner.LayoutLines(runs, baseStyle, maxWidth));
            Batches.Add(batch);
            return batch;
        }

        public LineHeightInfo MeasureLineHeight(TextStyle style) => _inner.MeasureLineHeight(style);
    }

    private sealed class TrackingBatch(ILineBatch inner) : ILineBatch
    {
        public bool Disposed { get; private set; }

        public int LineCount => inner.LineCount;

        public MeasuredLine GetLine(int index) => inner.GetLine(index);

        public object? NativeLayout => inner.NativeLayout;

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    private static void AssertOwnership(LayoutResult result, TrackingMeasurer measurer, string context)
    {
        var live = new HashSet<ILineBatch>(ReferenceEqualityComparer.Instance);
        foreach (var line in result.Lines)
        {
            if (line.Batch is not null)
            {
                live.Add(line.Batch);
            }
        }

        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            Assert.True(
                line.Batch is not TrackingBatch { Disposed: true },
                $"{context}：第 {i} 行（X={line.X}, Y={line.Y}）引用了已释放的批——" +
                "渲染层按批绘制会在 DrawTextLayout 抛 RO_E_CLOSED");
        }

        foreach (var batch in measurer.Batches)
        {
            Assert.True(
                live.Contains(batch) || batch.Disposed,
                $"{context}：既无人引用又未释放的批（泄漏）");
        }
    }

    // T-B4a：浮动悬在中央 → 一行横跨左右两段，两段各自建批；先建的批在后一段建批时被替换，
    // 但它的行盒仍在本次待提交行盒里（旧实现此处即时 Dispose → LayoutResult 持有僵尸批）。
    [Fact]
    public void TB4a_TwoSegmentRow_FirstSegmentBatchStaysAlive()
    {
        var measurer = new TrackingMeasurer();
        var engine = new FlowLayoutEngine(measurer);
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(20)) },
            new[] { Left(1, 30, 0, 40, 40) }, W);

        Assert.NotSame(result.Lines[0].Batch, result.Lines[1].Batch);
        AssertOwnership(result, measurer, "T-B4a");
    }

    // T-B4b：T-B2 的全输入矩阵逐例复核（含无浮动、双段、交错段、跨带交集重探、空段落）
    [Fact]
    public void TB4b_WholeMatrix_EveryReferencedBatchAliveAndEveryOrphanDisposed()
    {
        foreach (var row in BatchLayoutTests.EquivalenceCases())
        {
            string name = (string)row[0];
            var paragraphs = (ParagraphBlock[])row[1];
            var floats = (FloatObject[])row[2];

            var measurer = new TrackingMeasurer();
            var engine = new FlowLayoutEngine(measurer);
            using var result = engine.Layout(paragraphs, floats, W);

            AssertOwnership(result, measurer, name);
        }
    }

    // T-B4c：交集重探（行内大字撑高行高 → 段变窄 → 弃探测重排）确实产生弃批，且弃批必须被释放
    [Fact]
    public void TB4c_ReprobeDiscardedBatchIsDisposed()
    {
        var runs = new[]
        {
            new TextRun(FakeTextMeasurer.Text(5)),
            new TextRun(FakeTextMeasurer.Text(5), new InlineStyle(FontSizeRatio: 2f)),
            new TextRun(FakeTextMeasurer.Text(15)),
        };
        var measurer = new TrackingMeasurer();
        var engine = new FlowLayoutEngine(measurer);
        using var result = engine.Layout(
            new[] { new ParagraphBlock(runs) }, new[] { Left(1, 0, 10, 30, 30) }, W);

        AssertOwnership(result, measurer, "T-B4c");
        Assert.Contains(measurer.Batches, b => b.Disposed);
    }
}
