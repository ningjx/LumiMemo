using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Layout;

/// <summary>
/// Phase 3 M3：Todo 复选框命中（悬停光标与点击切换共用的纯判定）。
/// 假字体：字宽 10、行高 20；Todo 悬挂缩进 26（缩进区 = 行盒 X 左起的复选框绘制区）。
/// </summary>
public sealed class TodoCheckboxHitTests
{
    private const float W = 100f;

    private static LayoutResult Layout(params Block[] blocks) =>
        new FlowLayoutEngine(new FakeTextMeasurer()).Layout(new Document(blocks), W);

    [Fact]
    public void Hit_InIndentZone_ReturnsBlockIndex()
    {
        using var result = Layout(new TodoBlock("ab")); // 行盒 X=26，缩进区 [0, 26]
        Assert.Equal(0, result.HitTestTodoCheckbox(10, 10));
    }

    [Fact]
    public void Miss_OnTextArea()
    {
        using var result = Layout(new TodoBlock("ab"));
        Assert.Equal(-1, result.HitTestTodoCheckbox(40, 10));
    }

    [Fact]
    public void Miss_VerticallyOutsideLine()
    {
        using var result = Layout(new TodoBlock("ab"));
        Assert.Equal(-1, result.HitTestTodoCheckbox(10, 25));
    }

    [Fact]
    public void Hit_SecondBlock_ReturnsItsIndex()
    {
        using var result = Layout(new ParagraphBlock("p"), new TodoBlock("ab"));
        Assert.Equal(1, result.HitTestTodoCheckbox(10, 30)); // 第 2 块首行在 [20, 40)
        Assert.Equal(-1, result.HitTestTodoCheckbox(10, 10)); // 首块是正文，无复选框
    }

    [Fact]
    public void Miss_OnWrappedSecondLine_OnlyBlockStartHasCheckbox()
    {
        // 8 字符待办在 74 宽下折成两行：第 2 行同缩进，但不是块首行 → 不可点
        using var result = Layout(new TodoBlock(FakeTextMeasurer.Text(8)));
        Assert.Equal(0, result.HitTestTodoCheckbox(10, 10));
        Assert.Equal(-1, result.HitTestTodoCheckbox(10, 30));
    }

    [Fact]
    public void Miss_WithoutBlocksPayload()
    {
        // 旧签名（纯段落列表）排版：Blocks 为 null，无块元数据可判 → 不命中
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            [ParagraphBlock.FromText(FakeTextMeasurer.Text(5))], [], W);
        Assert.Equal(-1, result.HitTestTodoCheckbox(10, 10));
    }
}
