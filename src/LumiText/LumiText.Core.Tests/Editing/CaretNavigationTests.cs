using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// Phase 3 M6：光标导航（上下 / Home / End / Ctrl+Home+End / goal-X）。
/// 全部按假字体几何断言：字宽 10dip、行高 20dip、内容宽 200dip（每行 20 字）。
/// </summary>
public sealed class CaretNavigationTests
{
    private const float W = 200f;

    private static LayoutResult Layout(params Block[] blocks) =>
        new FlowLayoutEngine(new FakeTextMeasurer()).Layout(new Document(blocks), W);

    // ---------------- 上下移动 ----------------

    [Fact]
    public void Vertical_MovesOneVisualLineWithinParagraph()
    {
        using var layout = Layout(new ParagraphBlock(FakeTextMeasurer.Text(60))); // 3 行 × 20 字

        var down = CaretNavigator.Vertical(layout, new TextPosition(0, 25), goalX: 50f, down: true);
        Assert.Equal(new TextPosition(0, 45), down);

        var up = CaretNavigator.Vertical(layout, down, goalX: 50f, down: false);
        Assert.Equal(new TextPosition(0, 25), up);
    }

    [Fact]
    public void Vertical_GoalColumnSurvivesShortLine()
    {
        // 短行（10 字 = 100dip）夹在长行之间：期望列 200 穿过短行后仍回到长行行尾
        using var layout = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(20)));

        var up = CaretNavigator.Vertical(layout, new TextPosition(1, 20), goalX: 200f, down: false);
        Assert.Equal(new TextPosition(0, 10), up);              // 短行行尾

        var down = CaretNavigator.Vertical(layout, up, goalX: 200f, down: true);
        Assert.Equal(new TextPosition(1, 20), down);            // 期望列保持 → 回到长行行尾
    }

    [Fact]
    public void Vertical_GoalColumnRemainsWhenShortLineClamped()
    {
        // 反向：从短行行尾往下，期望列已在短行被钳过，仍应打到长行同一列
        using var layout = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(20)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));

        var down = CaretNavigator.Vertical(layout, new TextPosition(0, 15), goalX: 150f, down: true);
        Assert.Equal(new TextPosition(1, 10), down);            // 短行放不下 → 行尾
    }

    [Fact]
    public void Vertical_CrossesBlockBoundary()
    {
        using var layout = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(20)));

        var up = CaretNavigator.Vertical(layout, new TextPosition(1, 10), goalX: 100f, down: false);
        Assert.Equal(new TextPosition(0, 10), up);              // 上一块的末行行尾
    }

    [Fact]
    public void Vertical_AtDocumentEdges_Stays()
    {
        using var layout = Layout(new ParagraphBlock(FakeTextMeasurer.Text(60)));

        Assert.Equal(new TextPosition(0, 5),
            CaretNavigator.Vertical(layout, new TextPosition(0, 5), 50f, down: false)); // 首行往上
        Assert.Equal(new TextPosition(0, 55),
            CaretNavigator.Vertical(layout, new TextPosition(0, 55), 150f, down: true)); // 末行往下
    }

    [Fact]
    public void Vertical_SkipsDividerRow()
    {
        using var layout = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(4)),
            new DividerBlock(),
            new ParagraphBlock(FakeTextMeasurer.Text(4)));

        var down = CaretNavigator.Vertical(layout, new TextPosition(0, 0), goalX: 0f, down: true);
        Assert.Equal(new TextPosition(2, 0), down);             // 不停在分隔线占位行上
    }

    [Fact]
    public void Vertical_WithFloat_LandsOnNearestSegment()
    {
        // 图片 [100,200)×[0,60)：前三行被挤成图左 [0,100) / 图右 [200,300) 两段
        var blocks = new Block[] { new ParagraphBlock(FakeTextMeasurer.Text(70)) };
        var floats = new[] { new FloatObject(1, new LayoutRect(100f, 0f, 100f, 60f), FloatSide.Right) };
        using var layout = new FlowLayoutEngine(new FakeTextMeasurer())
            .Layout(blocks, floats, 300f);

        // 图左段：第 1 行第 6 字（块内 5）→ 第 2 行图左段同一列（块内 25）
        Assert.Equal(new TextPosition(0, 25),
            CaretNavigator.Vertical(layout, new TextPosition(0, 5), goalX: 54f, down: true));

        // 图右段：第 1 行第 6 字（块内 15，字左缘 X = 196 + 50）→ 第 2 行同列（块内 35）
        Assert.Equal(new TextPosition(0, 35),
            CaretNavigator.Vertical(layout, new TextPosition(0, 15), goalX: 246f, down: true));
    }

    [Fact]
    public void Vertical_OnEmptyBlock_MovesToNeighbourLine()
    {
        using var layout = Layout(
            new ParagraphBlock("abc"),
            new ParagraphBlock(""));

        // 空块占位行盒（无批）也能上下走
        var up = CaretNavigator.Vertical(layout, new TextPosition(1, 0), goalX: 0f, down: false);
        Assert.Equal(new TextPosition(0, 0), up);
    }

    // ---------------- Home / End ----------------

    [Fact]
    public void LineEdge_HomeEnd_OnWrappedLine()
    {
        using var layout = Layout(new ParagraphBlock(FakeTextMeasurer.Text(60)));

        Assert.Equal(new TextPosition(0, 20),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 25), toEnd: false));
        Assert.Equal(new TextPosition(0, 40),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 25), toEnd: true));
    }

    [Fact]
    public void LineEdge_AtSoftWrapBoundary_BelongsToNextLine()
    {
        // 软换行边界（第 20 字）：光标画在下一行行首（与光标几何同一归属）
        using var layout = Layout(new ParagraphBlock(FakeTextMeasurer.Text(60)));

        Assert.Equal(new TextPosition(0, 20),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 20), toEnd: false)); // Home 原地
        Assert.Equal(new TextPosition(0, 40),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 20), toEnd: true));  // End 走下一行行尾
    }

    [Fact]
    public void LineEdge_AtBlockEnd_IsBlockEnd()
    {
        using var layout = Layout(new ParagraphBlock(FakeTextMeasurer.Text(30)));

        Assert.Equal(new TextPosition(0, 30),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 30), toEnd: true));
        Assert.Equal(new TextPosition(0, 20),
            CaretNavigator.LineEdge(layout, new TextPosition(0, 30), toEnd: false)); // 末行行首
    }

    // ---------------- Ctrl+Home / Ctrl+End ----------------

    [Fact]
    public void DocumentEdge_SkipsImageBlocks()
    {
        Block[] blocks =
        [
            new ImageBlock("img", 40f, 30f),
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ImageBlock("img2", 40f, 30f),
        ];

        Assert.Equal(new TextPosition(1, 0), CaretNavigator.DocumentEdge(blocks, toEnd: false));
        Assert.Equal(new TextPosition(1, 10), CaretNavigator.DocumentEdge(blocks, toEnd: true));
    }

    [Fact]
    public void DocumentEdge_NoTextBlock_FallsBackToDocumentStart()
    {
        Block[] blocks = [new ImageBlock("img", 40f, 30f)];
        Assert.Equal(new TextPosition(0, 0), CaretNavigator.DocumentEdge(blocks, toEnd: true));
    }
}
