using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Layout;

/// <summary>
/// 批偏移换算（Phase 3 M4 修复）：批的文本 = <b>块文本从 <c>PlacedLine.BatchStart</c> 起的切片</b>
/// ——段一变（绕图、缩进变化）引擎就按剩余文本重建批。命中 / 光标 / 选区几何都必须做
/// 「块内偏移 ↔ 批内偏移」换算；漏了它，绕图段落的点击只会往段落前面偏，
/// 第一行图左段因为批从 0 起反而看着正常。
/// </summary>
public sealed class BatchOffsetTests
{
    private const float ContentWidth = 300f;

    /// <summary>图片 [100,200) × [0,60)：前三行被挤成图左 [0,100) / 图右 [200,300) 两段。</summary>
    private static FloatObject RightImage() =>
        new(1, new LayoutRect(100f, 0f, 100f, 60f), FloatSide.Right, 0f);

    private static LayoutResult Wrapped(string text) =>
        new FlowLayoutEngine(new FakeTextMeasurer()).Layout(
            [new ParagraphBlock(text)], [RightImage()], ContentWidth);

    // 70 字、每字 10dip：第 1–3 行每行两段各 10 字（块内 0–59），图片下方第 4 行整幅宽（60–69）。
    // 批起点：第 1 行图左 = 0、图右 = 10；第 2 行 = 20 / 30；第 3 行 = 40 / 50；第 4 行 = 60。

    [Fact]
    public void HitTest_WrappedParagraph_LandsOnClickedChar()
    {
        using var layout = Wrapped(FakeTextMeasurer.Text(70));

        // 第 1 行图右段第 6 个字 → 块内 15（批起点 10）
        Assert.Equal(15, layout.HitTest(250f, 5f).CharIndex);
        // 第 2 行图左段第 6 个字（x=54 落在该字左半）→ 块内 25（批起点 20）
        Assert.Equal(25, layout.HitTest(54f, 25f).CharIndex);
        // 第 4 行（图片下方，恢复整幅宽）第 6 个字 → 块内 65（批起点 60）
        Assert.Equal(65, layout.HitTest(54f, 65f).CharIndex);
    }

    [Fact]
    public void Caret_WrappedParagraph_UsesBatchOffset()
    {
        using var layout = Wrapped(FakeTextMeasurer.Text(70));

        // 块内第 25 个字 = 第 2 行图左段第 6 个：X = 5×10、Y = 行顶 20
        var caret = CaretGeometryCalculator.GetCaret(layout, new TextPosition(0, 25));
        Assert.NotNull(caret);
        Assert.Equal(50f, caret.Value.X);
        Assert.Equal(20f, caret.Value.Y);
    }

    [Fact]
    public void Selection_WrappedParagraph_UsesBatchOffset()
    {
        using var layout = Wrapped(FakeTextMeasurer.Text(70));

        // 选中第 2 行图左段的第 2–4 个字（块内 21–24）：X = 10、宽 30、Y = 20
        var rects = CaretGeometryCalculator.GetSelectionRects(layout,
            new TextRange(new TextPosition(0, 21), new TextPosition(0, 24)));
        var rect = Assert.Single(rects);
        Assert.Equal(10f, rect.X);
        Assert.Equal(20f, rect.Y);
        Assert.Equal(30f, rect.Width);
    }

    [Fact]
    public void FloatAnchor_OnWrappedParagraph_ReturnsBlockIndex()
    {
        using var layout = Wrapped(FakeTextMeasurer.Text(70));

        // 落点在第 4 行（批起点 60）左缘 54 → 锚点应是块内 65，不是批内 5
        var hit = layout.HitTestFloatAnchor(new LayoutRect(54f, 62f, 40f, 20f));
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(65, hit.CharIndex);
    }
}
