using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-B 系列：批量消费器的新增断言（Phase 1 设计 §10.1）——
/// T-B1 批数上界；T-B2 与逐行参照实现逐行一致；T-B3 行内大字撑高行高后跨带交集重探。
/// 假字体约定同 T1–T10：字宽 10、行高 20；内容宽 100 → 全宽行 10 字符。
/// </summary>
public sealed class BatchLayoutTests
{
    private const float W = 100f;

    private static FloatObject Left(int id, float x, float y, float w, float h) =>
        new(id, new LayoutRect(x, y, w, h), FloatSide.Left);

    private static FlowLayoutEngine NewEngine() => new(new FakeTextMeasurer());

    // T-B1a：无浮动单段落 → 整段一批（S2 性能解药的结构性断言）
    [Fact]
    public void TB1_SingleSegment_ExactlyOneBatch()
    {
        var engine = NewEngine();
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(25)) },
            Array.Empty<FloatObject>(), W);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(1, engine.LastStats.BatchesCreated);
        Assert.Equal(0, engine.LastStats.BatchesDiscarded);
    }

    // T-B1b：左浮图 → 段（X，宽）两种，批数 = 2，弃批 = 1（收窄批带未消费行被替换）
    [Fact]
    public void TB1_SegmentChange_BatchPerSegmentPlusDiscards()
    {
        var engine = NewEngine();
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(25)) },
            new[] { Left(1, 0, 0, 30, 60) }, W);

        Assert.Equal(4, result.Lines.Count);  // T2 行为不变
        Assert.Equal(2, engine.LastStats.BatchesCreated);
        Assert.Equal(1, engine.LastStats.BatchesDiscarded);
        // 不变式：批数 ≤ 段（X，宽）种数 + 弃批数
        Assert.True(engine.LastStats.BatchesCreated <= 2 + engine.LastStats.BatchesDiscarded);
    }

    // T-B2：批量结果与逐行旧逻辑（参照实现）逐行一致——T1–T10 同款输入矩阵
    [Theory]
    [MemberData(nameof(EquivalenceCases))]
    public void TB2_BatchMatchesFirstLineReference(string name, ParagraphBlock[] paragraphs, FloatObject[] floats)
    {
        _ = name;
        var engine = NewEngine();
        using var result = engine.Layout(paragraphs, floats, W);
        var reference = ReferenceLayout.Layout(paragraphs, floats, W);

        Assert.Equal(reference.Count, result.Lines.Count);
        for (int i = 0; i < reference.Count; i++)
        {
            var expected = reference[i];
            var actual = result.Lines[i];
            Assert.Equal(expected.ParagraphIndex, actual.ParagraphIndex);
            Assert.Equal(expected.CharStart, actual.CharStart);
            Assert.Equal(expected.CharCount, actual.CharCount);
            Assert.Equal(expected.X, actual.X, 2);
            Assert.Equal(expected.Y, actual.Y, 2);
            Assert.Equal(expected.Width, actual.Width, 2);
            Assert.Equal(expected.Baseline, actual.Baseline, 2);
        }
    }

    public static IEnumerable<object[]> EquivalenceCases()
    {
        static ParagraphBlock[] P(params string[] texts) =>
            texts.Select(ParagraphBlock.FromText).ToArray();
        static ParagraphBlock[] P2(string a, float spaceAfter, string b) =>
            new[] { new ParagraphBlock(a, spaceAfter: spaceAfter), new ParagraphBlock(b) };

        var right60 = new FloatObject(1, new LayoutRect(60, 0, 40, 60), FloatSide.Right);
        var right70 = new FloatObject(2, new LayoutRect(70, 0, 30, 20), FloatSide.Right);
        var margin = new FloatObject(1, new LayoutRect(0, 0, 30, 20), FloatSide.Left, Margin: 10f);
        var overlap1 = Left(1, 0, 0, 30, 40);
        var overlap2 = Left(2, 0, 20, 40, 40);

        string t5 = FakeTextMeasurer.Text(5);
        string t8 = FakeTextMeasurer.Text(8);
        string t10 = FakeTextMeasurer.Text(10);
        string t20 = FakeTextMeasurer.Text(20);
        string t25 = FakeTextMeasurer.Text(25);
        string t30 = FakeTextMeasurer.Text(30);

        // T1–T10 + margin + 空段落 + 多段落（与 FlowLayoutEngineTests 同输入）
        yield return new object[] { "T1", P(t25), Array.Empty<FloatObject>() };
        yield return new object[] { "T2", P(t25), new[] { Left(1, 0, 0, 30, 60) } };
        yield return new object[] { "T3", P(t25), new[] { right60 } };
        yield return new object[] { "T4", P(t8), new FloatObject[] { Left(1, 0, 0, 30, 20), right70 } };
        yield return new object[] { "T5", P(t20), new[] { Left(1, 0, 0, 30, 10) } };
        yield return new object[] { "T6", P(t10), new[] { Left(1, 0, 0, 95, 20) } };
        yield return new object[] { "T7", P2(t10, 5f, t10), new[] { Left(1, 0, 0, 30, 50) } };
        yield return new object[] { "T8a", P(t20), new[] { Left(1, 0, 0, 30, 40) } };
        yield return new object[] { "T8b", P(t20), new[] { Left(1, 70, 0, 30, 40) } };
        yield return new object[] { "T9", P(t20), new FloatObject[] { overlap1, overlap2 } };
        yield return new object[] { "T10", P(t30), new[] { Left(1, 0, 0, 30, 40) } };
        yield return new object[] { "margin", P(t10), new[] { margin } };
        yield return new object[] { "empty", P(t10, string.Empty, t5), Array.Empty<FloatObject>() };
        // 交错段（浮动悬在中央，一行横跨左右两段）：批量退化为逐行建批但结果必须一致
        yield return new object[] { "center", P(t20), new[] { Left(1, 30, 0, 40, 40) } };
    }

    // T-B3：行内大字（FontSizeRatio 2）撑高行高后，跨带交集重探仍取最窄约束
    [Fact]
    public void TB3_InlineTallLine_CrossBandReprobeUsesIntersection()
    {
        // 25 字符：前 5 正常 + 5 个大字（行高 40）+ 15 正常；浮动带 [10,40) 排除左 30。
        var runs = new[]
        {
            new TextRun(FakeTextMeasurer.Text(5)),
            new TextRun(FakeTextMeasurer.Text(5), new InlineStyle(FontSizeRatio: 2f)),
            new TextRun(FakeTextMeasurer.Text(15)),
        };
        var engine = NewEngine();
        using var result = engine.Layout(
            new[] { new ParagraphBlock(runs) },
            new[] { Left(1, 0, 10, 30, 30) }, W);

        // 首行高 40（y=0..40），横跨带 [0,10) 与 [10,40)：交集段 [30,100]（宽 70）→ 6 字符
        // （全宽探测会给 7 字符——交集重探生效的直接证据；且没有因行高 40 被推空留洞）。
        Assert.Equal(30f, result.Lines[0].X);
        Assert.Equal(6, result.Lines[0].CharCount);
        Assert.Equal(40f, result.Lines[0].Height);
        Assert.Equal(32f, result.Lines[0].Baseline);   // 16 × 2
        // 第二行 y=40 起恢复全宽（浮动底缘恰在首行底）。
        Assert.Equal(0f, result.Lines[1].X);
        Assert.Equal(40f, result.Lines[1].Y);
        // 字符无缝衔接：6 + 后续行合计 = 25。
        Assert.Equal(25, result.Lines.Sum(l => l.CharCount));
    }
}
