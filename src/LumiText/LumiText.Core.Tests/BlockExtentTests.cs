using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// Phase 3 M1：块几何产物 <see cref="BlockExtent"/>（§4）。
/// 假字体约定：字宽 10、行高 20（Ascent 16 + Descent 4）；内容宽 100 → 全宽行 10 字符。
/// 背景垂直内边距初值 4，按相邻有行盒块的间距一半钳制。
/// </summary>
public sealed class BlockExtentTests
{
    private const float W = 100f;
    private static readonly Color32 Bg = new(0x40, 0xFF, 0xD9, 0x66);

    private static FlowLayoutEngine NewEngine() => new(new FakeTextMeasurer());

    [Fact]
    public void NoBackground_NoExtents()
    {
        using var result = NewEngine().Layout(
            [new ParagraphBlock("abc")], Array.Empty<FloatObject>(), W);
        Assert.Empty(result.BlockExtents);
    }

    [Fact]
    public void SingleBlock_ExtentCoversFullColumn_AndExtendsTotalHeight()
    {
        using var result = NewEngine().Layout(
            [new ParagraphBlock("abc") { Background = Bg }], Array.Empty<FloatObject>(), W);

        var extent = Assert.Single(result.BlockExtents);
        Assert.Equal(0, extent.BlockIndex);
        // 首块上边距被「与文档顶的间距 0」钳到 0；末块下边距取满 4
        Assert.Equal(new LayoutRect(0f, 0f, W, 24f), extent.Rect);
        // 底边距进入文档总高（否则末块背景被 surface 裁掉）
        Assert.Equal(24f, result.TotalHeight);
    }

    [Fact]
    public void MultiLineBlock_ExtentIsUnionOfLines()
    {
        // 25 字符 → 3 行（10/10/5），高 60
        using var result = NewEngine().Layout(
            [new ParagraphBlock(FakeTextMeasurer.Text(25)) { Background = Bg }],
            Array.Empty<FloatObject>(), W);

        var extent = Assert.Single(result.BlockExtents);
        Assert.Equal(new LayoutRect(0f, 0f, W, 64f), extent.Rect);
    }

    [Fact]
    public void Padding_ClampedByGapBetweenBlocks()
    {
        Block[] blocks =
        {
            new ParagraphBlock("a", spaceAfter: 20f) { Background = Bg },
            new ParagraphBlock("b") { Background = Bg },
        };
        using var result = NewEngine().Layout(blocks, Array.Empty<FloatObject>(), W);

        Assert.Equal(2, result.BlockExtents.Count);
        // A：底 20，与 B 顶（40）间距 20 → 下边距 min(4, 10) = 4
        Assert.Equal(new LayoutRect(0f, 0f, W, 24f), result.BlockExtents[0].Rect);
        // B：顶 40，与 A 底间距 20 → 上边距 4 → 起点 36；末块下边距取满 4
        Assert.Equal(new LayoutRect(0f, 36f, W, 28f), result.BlockExtents[1].Rect);
        Assert.Equal(64f, result.TotalHeight);
    }

    [Fact]
    public void AdjacentBlocks_ZeroGap_NoOverlap()
    {
        Block[] blocks =
        {
            new ParagraphBlock("a") { Background = Bg },
            new ParagraphBlock("b") { Background = Bg },
        };
        using var result = NewEngine().Layout(blocks, Array.Empty<FloatObject>(), W);

        var first = result.BlockExtents[0].Rect;
        var second = result.BlockExtents[1].Rect;
        Assert.Equal(new LayoutRect(0f, 0f, W, 20f), first);   // 零间距 → 下边距 0
        Assert.Equal(new LayoutRect(0f, 20f, W, 24f), second); // 上边距 0，紧贴不重叠
        Assert.Equal(first.Bottom, second.Y);
    }

    [Fact]
    public void TodoBlock_Indent_DoesNotNarrowExtent()
    {
        using var result = NewEngine().Layout(
            [new TodoBlock("ab") { Background = Bg }], Array.Empty<FloatObject>(), W);

        var extent = Assert.Single(result.BlockExtents);
        Assert.Equal(0f, extent.Rect.X);      // 整列口径：缩进只影响行盒，不影响底色范围
        Assert.Equal(W, extent.Rect.Width);
    }

    [Fact]
    public void OnlyBackgroundBlocks_ProduceExtents()
    {
        Block[] blocks =
        {
            new ParagraphBlock("a"),
            new ParagraphBlock("b") { Background = Bg },
            new ParagraphBlock("c"),
        };
        using var result = NewEngine().Layout(blocks, Array.Empty<FloatObject>(), W);

        var extent = Assert.Single(result.BlockExtents);
        Assert.Equal(1, extent.BlockIndex);
        // 上下间距各 0 → 上下边距都被钳到 0
        Assert.Equal(new LayoutRect(0f, 20f, W, 20f), extent.Rect);
    }
}
