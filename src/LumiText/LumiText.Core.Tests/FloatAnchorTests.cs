using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-C3 系列：浮动锚定两遍排版的锚点解析（字符级锚定，2026-10-03 补丁）。
/// 假字体约定：字宽 10、行高 20；内容宽 100 → 全宽行 10 字符。
/// </summary>
public sealed class FloatAnchorTests
{
    private const float W = 100f;

    private static FlowLayoutEngine NewEngine() => new(new FakeTextMeasurer());

    private static FloatObject AnchoredFloat(
        int id, float w, float h, FloatSide side, int anchorBlock, int anchorChar) =>
        new(id, new LayoutRect(0, 0, w, h), side, Margin: 0f)
        {
            Anchor = new FloatAnchor(anchorBlock, anchorChar),
        };

    // T-C3a：锚定块首字符（CharIndex=0）→ 浮动落在该块首行顶缘；文字环绕解析后的位置
    [Fact]
    public void TC3a_AnchorToFirstChar_FloatLandsOnFirstLineTop()
    {
        var engine = NewEngine();
        Block[] blocks = { ParagraphBlock.FromText(FakeTextMeasurer.Text(10)) };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 0, anchorChar: 0) };

        using var result = engine.Layout(blocks, floats, W);

        Assert.Equal(0f, result.Floats[0].Rect.X);
        Assert.Equal(0f, result.Floats[0].Rect.Y);
        // 第二遍排版：浮动 [0,20) 占左 30 → 首行缩进段 [30,100]（7 字符），越过底部恢复全宽
        Assert.Equal(30f, result.Lines[0].X);
        Assert.Equal(7, result.Lines[0].CharCount);
        Assert.Equal(0f, result.Lines[1].X);
    }

    // T-C3b：锚到第二行的首字符（CharIndex=10）→ 浮动顶缘 = 第二行行盒 Y；右浮贴右缘
    [Fact]
    public void TC3b_AnchorToSecondLineChar_FloatLandsOnThatLine()
    {
        var engine = NewEngine();
        Block[] blocks = { ParagraphBlock.FromText(FakeTextMeasurer.Text(25)) };   // 3 行（10+10+5）
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Right, anchorBlock: 0, anchorChar: 10) };

        using var result = engine.Layout(blocks, floats, W);

        // 第二行 Y = 20；右浮 X = 100 − 30 = 70
        Assert.Equal(20f, result.Floats[0].Rect.Y);
        Assert.Equal(70f, result.Floats[0].Rect.X);
    }

    // T-C3c：CharIndex 越界（≥ 块总字符数）→ 钳到该块末行
    [Fact]
    public void TC3c_AnchorCharOutOfRange_ClampsToLastLine()
    {
        var engine = NewEngine();
        Block[] blocks = { ParagraphBlock.FromText(FakeTextMeasurer.Text(25)) };   // 3 行（10+10+5）
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 0, anchorChar: 999) };

        using var result = engine.Layout(blocks, floats, W);

        // 末行 Y = 40
        Assert.Equal(40f, result.Floats[0].Rect.Y);
    }

    // T-C3d：锚到非文本块（Divider）→ 顺延到其后第一个文本块（首字符）
    [Fact]
    public void TC3d_AnchorToDivider_FallsThroughToNextTextBlock()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            new DividerBlock(),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
        };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 1, anchorChar: 0) };

        using var result = engine.Layout(blocks, floats, W);

        // 块 2 首行 Y = 20（块 0）+ 20（Divider 占位）= 40
        Assert.Equal(40f, result.Floats[0].Rect.Y);
    }

    // T-C3e：锚点块索引越界 → 钳到最后一块（首字符）
    [Fact]
    public void TC3e_AnchorBlockIndexOutOfRange_ClampsToLastBlock()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
        };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 99, anchorChar: 0) };

        using var result = engine.Layout(blocks, floats, W);

        // 钳到块 1，其首行 Y = 20
        Assert.Equal(20f, result.Floats[0].Rect.Y);
    }

    // T-C3f：全文无文本行盒 → 浮动退化为 Rect 直给路径
    [Fact]
    public void TC3f_NoTextBlocks_FallsBackToRectPath()
    {
        var engine = NewEngine();
        Block[] blocks = { new DividerBlock() };
        var anchored = AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 0, anchorChar: 0);
        var direct = anchored with { Rect = new LayoutRect(24, 48, 30, 20) };

        using var result = engine.Layout(blocks, new[] { direct }, W);

        // 锚点解析失败 → 保留调用方直给矩形（X=24, Y=48），经 PlaceFloats 归一化不变
        Assert.Equal(24f, result.Floats[0].Rect.X);
        Assert.Equal(48f, result.Floats[0].Rect.Y);
        Assert.Single(result.Lines);   // Divider 占位行盒仍在（不受浮动影响的证明略）
    }
}
