using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-C 系列：块级排版（Phase 1 设计 §10.1）——
/// T-C1 空段落/Divider 占位高度；T-C2 Todo 悬挂缩进（含浮动叠加与窄段放弃）；
/// T-C3 浮动锚定两遍排版的锚点解析（另见 FloatAnchorTests）。
/// 假字体约定同 T1–T10：字宽 10、行高 20；内容宽 100 → 全宽行 10 字符。
/// </summary>
public sealed class BlockLayoutTests
{
    private const float W = 100f;

    private static FlowLayoutEngine NewEngine() => new(new FakeTextMeasurer());

    private static FloatObject Left(int id, float x, float y, float w, float h) =>
        new(id, new LayoutRect(x, y, w, h), FloatSide.Left);

    // T-C1：Divider 占位行盒 = 默认正文行高，文档顺序推进
    [Fact]
    public void TC1_Divider_OccupiesPlaceholderLine()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            new DividerBlock(),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(5)),
        };
        using var result = engine.Layout(blocks, Array.Empty<FloatObject>(), W);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(0f, result.Lines[0].Y);
        Assert.Equal(PlacedLineKind.Text, result.Lines[0].Kind);
        Assert.True(result.Lines[0].IsBlockStart);

        var divider = result.Lines[1];
        Assert.Equal(1, divider.BlockIndex);
        Assert.Equal(PlacedLineKind.Divider, divider.Kind);
        Assert.Equal(20f, divider.Y);
        Assert.Equal(20f, divider.Height);   // TextStyle.Default 空行高度（假字体行高 20）
        Assert.Equal(0, divider.CharCount);
        Assert.Null(divider.Batch);
        Assert.True(divider.IsBlockStart);

        Assert.Equal(2, result.Lines[2].BlockIndex);
        Assert.Equal(40f, result.Lines[2].Y);
        Assert.Equal(60f, result.TotalHeight);
    }

    // T-C1b：空段落占空行高度，与 Divider 叠加推进
    // （M8 起空块也产出行盒——否则空 bullet/todo 的标记画不出、空段落点击无落点、光标无处显示；
    // 行数因此 = 空段落占位 1 + Divider 1 + 正文 1）
    [Fact]
    public void TC1_EmptyParagraphAndDivider_StackCorrectly()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(string.Empty),
            new DividerBlock(),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(5)),
        };
        using var result = engine.Layout(blocks, Array.Empty<FloatObject>(), W);

        Assert.Equal(3, result.Lines.Count);

        var empty = result.Lines[0];
        Assert.Equal(PlacedLineKind.Text, empty.Kind);
        Assert.Equal(0, empty.BlockIndex);
        Assert.Equal(0, empty.CharCount);
        Assert.Null(empty.Batch);           // 占位行盒无批：命中/光标走专门路径
        Assert.True(empty.IsBlockStart);
        Assert.Equal(0f, empty.Y);

        Assert.Equal(PlacedLineKind.Divider, result.Lines[1].Kind);
        Assert.Equal(20f, result.Lines[1].Y);   // 空段落 20 + Divider 起于 20
        Assert.Equal(40f, result.Lines[2].Y);
        Assert.Equal(60f, result.TotalHeight);
    }

    // T-C2：Todo 悬挂缩进——所有行（含换行）X = 段 X + LeftIndent
    [Fact]
    public void TC2_TodoHangingIndent_AllLinesShifted()
    {
        var engine = NewEngine();
        Block[] blocks = { new TodoBlock(FakeTextMeasurer.Text(20)) };
        using var result = engine.Layout(blocks, Array.Empty<FloatObject>(), W);

        // 段宽 100 − 26 = 74 → 每行 7 字符（70 ≤ 74）
        Assert.Equal(new[] { 7, 7, 6 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.All(result.Lines, l =>
        {
            Assert.Equal(26f, l.X);
            Assert.Equal(PlacedLineKind.TodoText, l.Kind);
        });
        Assert.True(result.Lines[0].IsBlockStart);
        Assert.False(result.Lines[1].IsBlockStart);
        Assert.False(result.Lines[2].IsBlockStart);
    }

    // T-C2b：缩进与浮动排除区叠加——缩进在段宽收窄之后生效
    [Fact]
    public void TC2_TodoIndent_AppliesAfterFloatNarrowing()
    {
        var engine = NewEngine();
        Block[] blocks = { new TodoBlock(FakeTextMeasurer.Text(20)) };
        using var result = engine.Layout(blocks, new[] { Left(1, 0, 0, 30, 40) }, W);

        // 带 [0,40)：段 [30,100] 宽 70 → 缩进后 [56,100] 宽 44 → 4 字符/行
        Assert.Equal(56f, result.Lines[0].X);
        Assert.Equal(4, result.Lines[0].CharCount);
        Assert.Equal(56f, result.Lines[1].X);
        // 越过浮动底部：段 [0,100] → 缩进后 [26,100] 宽 74 → 7 字符/行
        Assert.Equal(26f, result.Lines[2].X);
        Assert.Equal(40f, result.Lines[2].Y);
        Assert.Equal(7, result.Lines[2].CharCount);
        Assert.Equal(20, result.Lines.Sum(l => l.CharCount));
    }

    // T-C2c：段宽 < LeftIndent + 最小字宽 → 按窄段放弃（不死循环）
    [Fact]
    public void TC2_TodoIndent_NarrowSegmentAbandoned()
    {
        var engine = NewEngine();
        Block[] blocks = { new TodoBlock(FakeTextMeasurer.Text(10)) };
        using var result = engine.Layout(blocks, new[] { Left(1, 0, 0, 70, 20) }, W);

        // 带 [0,20)：段 [70,100] 宽 30 → 缩进后宽 4 < 字宽 10 → 放弃，文本推到浮动之下
        // （缩进后宽 74 → 7 字符/行，10 字符分两行；断言要点是 y ≥ 20 且 X = 26、无死循环）
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(20f, result.Lines[0].Y);
        Assert.Equal(26f, result.Lines[0].X);
        Assert.Equal(7, result.Lines[0].CharCount);
        Assert.Equal(10, result.Lines.Sum(l => l.CharCount));
    }

    // 块驱动的回归：LayoutResult.Blocks 透传 + Document 入口
    [Fact]
    public void DocumentEntry_PassesBlocksThrough()
    {
        var doc = new Document(
            new Block[]
            {
                new HeadingBlock("标题", 2),
                new TodoBlock("待办", @checked: true),
            });
        var engine = NewEngine();
        using var result = engine.Layout(doc, W);

        Assert.NotNull(result.Blocks);
        Assert.Equal(2, result.Blocks!.Count);
        Assert.IsType<HeadingBlock>(result.Blocks[0]);
        Assert.IsType<TodoBlock>(result.Blocks[1]);
        Assert.Equal(PlacedLineKind.Text, result.Lines[0].Kind);
        Assert.Equal(PlacedLineKind.TodoText, result.Lines[1].Kind);
    }
}
