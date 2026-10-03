using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Layout;

/// <summary>
/// M2 字符级命中/光标/选区几何测试（假字体确定性断言）。
/// 假字体：字符宽 10dip，行高 20（Ascent 16 + Descent 4）。
/// </summary>
public sealed class HitTestCharLevelTests
{
    private const float W = 200f; // 内容宽（每行 20 字符）

    private static LayoutResult Layout(params Block[] blocks)
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        return engine.Layout(new Document(blocks), W);
    }

    [Fact]
    public void HitTest_FirstCharLeadingHit_ReturnsCharIndex0()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(20)));
        // 点第 0 字符的左半（x=3 < 5 = CharWidth/2）
        var hit = result.HitTest(3, 5);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.CharIndex);
        Assert.False(hit.IsTrailingHit);
    }

    [Fact]
    public void HitTest_FirstCharTrailingHit_ReturnsCharIndex1()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(20)));
        // 点第 0 字符的右半（x=7 >= 5 = CharWidth/2）→ 落尾，光标在字符之后
        var hit = result.HitTest(7, 5);
        Assert.True(hit.Found);
        Assert.Equal(1, hit.CharIndex);
        Assert.True(hit.IsTrailingHit);
    }

    [Fact]
    public void HitTest_OnDivider_LandsOnNearestTextLine()
    {
        // 分隔线占位行盒（Y=20..40）没有可放光标的文本位置：点它落到最近的文本行
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(4)),
            new DividerBlock(),
            new ParagraphBlock(FakeTextMeasurer.Text(4)));

        var hit = result.HitTest(24, 25); // 离上一行（底缘 20）比下一行（顶缘 40）近
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(2, hit.CharIndex);
    }

    [Fact]
    public void HitTest_MiddleOfLine_CharIndexMatchesOffset()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(20)));
        // 点第 5 字符左半（x=53 → charOffset=5, isTrailing=false）
        var hit = result.HitTest(53, 5);
        Assert.True(hit.Found);
        Assert.Equal(5, hit.CharIndex);
    }

    [Fact]
    public void HitTest_SecondLine_CharIndexIsLineRelative()
    {
        // 25 字符分两行（20/行）：第 2 行 CharStart=20
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(25)));
        // 点第 2 行第 2 字符左半（x=23, y=25 在第二行 [20,40)）
        var hit = result.HitTest(23, 25);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        // CharIndex 是块内偏移：20（行首）+ 2 = 22
        Assert.Equal(22, hit.CharIndex);
    }

    [Fact]
    public void HitTest_SecondBlock_CharIndexIsBlockRelative()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));
        // 点第 2 块第 3 字符（y=25 在第 2 块首行，x=33 → charOffset=3）
        var hit = result.HitTest(33, 25);
        Assert.True(hit.Found);
        Assert.Equal(1, hit.BlockIndex);
        Assert.Equal(3, hit.CharIndex);
    }

    [Fact]
    public void HitTest_CaretPosition_IsTextPosition()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = result.HitTest(7, 5);
        Assert.Equal(new TextPosition(0, 1), hit.CaretPosition);
    }

    [Fact]
    public void GetCaret_AtLineStart_XIsZero()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(0, 0));
        Assert.NotNull(caret);
        Assert.Equal(0f, caret.Value.X);
        Assert.Equal(0f, caret.Value.Y);
        Assert.Equal(20f, caret.Value.Height);
    }

    [Fact]
    public void GetCaret_AtCharIndex5_XIs50()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(0, 5));
        Assert.NotNull(caret);
        Assert.Equal(50f, caret.Value.X);
    }

    [Fact]
    public void GetCaret_AtLineEnd_XIsLineWidth()
    {
        // 10 字符占满一行（10 × 10dip = 100dip），行尾光标 X = 100
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(0, 10));
        Assert.NotNull(caret);
        Assert.Equal(100f, caret.Value.X);
    }

    [Fact]
    public void GetCaret_SecondLine_YIsLineHeight()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(25)));
        // 第 2 行行首（块内偏移 20）
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(0, 20));
        Assert.NotNull(caret);
        Assert.Equal(20f, caret.Value.Y); // 第 2 行顶缘
        Assert.Equal(0f, caret.Value.X);
    }

    [Fact]
    public void GetSelectionRects_WithinLine_SingleRect()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var range = new TextRange(new TextPosition(0, 2), new TextPosition(0, 5));
        var rects = CaretGeometryCalculator.GetSelectionRects(result, range);
        Assert.Single(rects);
        Assert.Equal(20f, rects[0].X); // 字符 2 起
        Assert.Equal(30f, rects[0].Width); // 3 字符宽
        Assert.Equal(20f, rects[0].Height);
    }

    [Fact]
    public void GetSelectionRects_AcrossLines_TwoRects()
    {
        // 20 字符占满一行，选区从首行第 15 字符到第 2 行第 5 字符
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(25)));
        var range = new TextRange(new TextPosition(0, 15), new TextPosition(0, 22));
        var rects = CaretGeometryCalculator.GetSelectionRects(result, range);
        Assert.Equal(2, rects.Count);
        // 第 1 行：字符 15–19（5 字符宽）
        Assert.Equal(150f, rects[0].X);
        Assert.Equal(50f, rects[0].Width);
        // 第 2 行：字符 20–21（2 字符宽）
        Assert.Equal(0f, rects[1].X);
        Assert.Equal(20f, rects[1].Width);
        Assert.Equal(20f, rects[1].Y);
    }

    [Fact]
    public void GetSelectionRects_AcrossBlocks_TwoRects()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var range = new TextRange(new TextPosition(0, 5), new TextPosition(1, 3));
        var rects = CaretGeometryCalculator.GetSelectionRects(result, range);
        Assert.Equal(2, rects.Count);
        Assert.Equal(0, result.Lines[0].BlockIndex);
        Assert.Equal(1, result.Lines[1].BlockIndex);
    }

    [Fact]
    public void GetSelectionRects_Collapsed_ReturnsEmpty()
    {
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var range = TextRange.Collapse(new TextPosition(0, 5));
        var rects = CaretGeometryCalculator.GetSelectionRects(result, range);
        Assert.Empty(rects);
    }

    // ---- 空块占位行盒（无批）的命中/光标路径 ----

    [Fact]
    public void HitTest_EmptyParagraph_HitsBlockStart()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(""));
        // 空块占位行盒在 [20,40)：点击落点 = (1, 0)
        var hit = result.HitTest(50, 25);
        Assert.True(hit.Found);
        Assert.Equal(1, hit.BlockIndex);
        Assert.Equal(0, hit.CharIndex);
    }

    [Fact]
    public void GetCaret_EmptyParagraph_PlacesholderLineOrigin()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(""));
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(1, 0));
        Assert.NotNull(caret);
        Assert.Equal(0f, caret.Value.X);
        Assert.Equal(20f, caret.Value.Y);
        Assert.Equal(20f, caret.Value.Height);
    }

    [Fact]
    public void GetCaret_EmptyBullet_IndentedOrigin()
    {
        using var result = Layout(new ParagraphBlock("", isBullet: true));
        var caret = CaretGeometryCalculator.GetCaret(result, new TextPosition(0, 0));
        Assert.NotNull(caret);
        Assert.Equal(16f, caret.Value.X); // 光标在缩进之后（bullet 圆点左侧区域留给标记）
    }

    // ---- 空白区兜底（Phase 3 修复：行尾空白可点、拖选经过空白不断）----

    [Fact]
    public void HitTest_RightOfShortLine_CaretAtLineEnd()
    {
        // 5 字符行（宽 50）：点行尾右侧空白 → 行尾
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(5)));
        var hit = result.HitTest(150, 5);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(5, hit.CharIndex);
        Assert.True(hit.IsTrailingHit);
    }

    [Fact]
    public void HitTest_SecondLineRightBlank_CaretAtBlockEnd()
    {
        // 25 字符两行（20/5）：点第 2 行右侧空白 → 块内 25（第 2 行行尾）
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(25)));
        var hit = result.HitTest(180, 25);
        Assert.True(hit.Found);
        Assert.Equal(25, hit.CharIndex);
    }

    [Fact]
    public void HitTest_LeftOfIndentedLine_CaretAtLineStart()
    {
        // Todo 悬挂缩进 26、文本宽 20：点缩进区左侧 → 行首
        // （复选框区的点击语义由编辑器层在命中测试之前截获，布局层只管光标落点）
        using var result = Layout(new TodoBlock("ab"));
        var hit = result.HitTest(8, 5);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(0, hit.CharIndex);
        Assert.False(hit.IsTrailingHit);
    }

    [Fact]
    public void HitTest_InFloatArea_FallsBackToNearestSegment()
    {
        // 左浮动占 [0,60]：行段 X=60；点浮动区 x=10 → 最近段行首
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var floats = new[] { new FloatObject(0, new LayoutRect(0, 0, 60, 20), FloatSide.Left) };
        using var result = engine.Layout(
            [new ParagraphBlock(FakeTextMeasurer.Text(3))], floats, W);

        var hit = result.HitTest(10, 5);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(0, hit.CharIndex);
    }

    [Fact]
    public void HitTest_AboveFirstLine_CaretFollowsX()
    {
        // 首行之上但 x 落在行文本推进宽度内 → 落到最近行、按 x 定列（y 钳进行内）
        using var result = Layout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = result.HitTest(40, -30);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(4, hit.CharIndex);
    }

    [Fact]
    public void HitTest_BelowLastLine_CaretOnNearestLineAtX()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = result.HitTest(30, 200);
        Assert.True(hit.Found);
        Assert.Equal(1, hit.BlockIndex);
        Assert.Equal(3, hit.CharIndex);
    }

    [Fact]
    public void HitTest_BelowLastLine_RightOfText_CaretAtLineEnd()
    {
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = result.HitTest(500, 200);
        Assert.True(hit.Found);
        Assert.Equal(1, hit.BlockIndex);
        Assert.Equal(10, hit.CharIndex);
        Assert.True(hit.IsTrailingHit);
    }

    [Fact]
    public void HitTest_InGapBetweenBlocks_NearestLineWins()
    {
        // 块 0 有 spaceAfter 30 → 间距带 [20,50)；y=35 距上/下各 15（并列）→ 文档序在前者（上一块末行），
        // x=60 落在其文本推进宽度内 → 按 x 定列
        using var result = Layout(
            new ParagraphBlock(FakeTextMeasurer.Text(10), spaceAfter: 30f),
            new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = result.HitTest(60, 35);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(6, hit.CharIndex);
    }
}
