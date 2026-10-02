using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-C3 系列：浮动锚定两遍排版的锚点解析（Phase 1 设计 §6.3/§10.1）。
/// 假字体约定：字宽 10、行高 20；内容宽 100 → 全宽行 10 字符。
/// </summary>
public sealed class FloatAnchorTests
{
    private const float W = 100f;

    private static FlowLayoutEngine NewEngine() => new(new FakeTextMeasurer());

    private static FloatObject AnchoredFloat(
        int id, float w, float h, FloatSide side, int anchorBlock, float offsetX, float offsetY) =>
        new(id, new LayoutRect(0, 0, w, h), side, Margin: 0f)
        {
            Anchor = new FloatAnchor(anchorBlock, offsetX, offsetY),
        };

    // T-C3a：锚定块首 + 零偏移 → 浮动落在该块首行顶缘；文字环绕解析后的位置
    [Fact]
    public void TC3a_AnchorToBlockStart_FloatLandsOnFirstLineTop()
    {
        var engine = NewEngine();
        Block[] blocks = { ParagraphBlock.FromText(FakeTextMeasurer.Text(10)) };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 0, 0, 0) };

        using var result = engine.Layout(blocks, floats, W);

        Assert.Equal(0f, result.Floats[0].Rect.X);
        Assert.Equal(0f, result.Floats[0].Rect.Y);
        // 第二遍排版：浮动 [0,20) 占左 30 → 首行缩进段 [30,100]（7 字符），越过底部恢复全宽
        Assert.Equal(30f, result.Lines[0].X);
        Assert.Equal(7, result.Lines[0].CharCount);
        Assert.Equal(0f, result.Lines[1].X);
    }

    // T-C3b：锚到第二块 + OffsetY → 顶缘 = 第二块首行 Y + 偏移；右浮 OffsetX 相对右缘
    [Fact]
    public void TC3b_AnchorToSecondBlockWithOffsets()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10), spaceAfter: 10f),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
        };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Right, anchorBlock: 1, 8, 4) };

        using var result = engine.Layout(blocks, floats, W);

        // 第一遍：块 1 首行 Y = 20（块 0 一行）+ 10（段后距）= 30 → 浮动顶缘 34
        Assert.Equal(34f, result.Floats[0].Rect.Y);
        // 右浮：X = 100 − 30 − 8 = 62
        Assert.Equal(62f, result.Floats[0].Rect.X);
    }

    // T-C3c：锚到非文本块（Divider）→ 顺延到其后第一个文本块
    [Fact]
    public void TC3c_AnchorToDivider_FallsThroughToNextTextBlock()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            new DividerBlock(),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
        };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 1, 0, 0) };

        using var result = engine.Layout(blocks, floats, W);

        // 块 2 首行 Y = 20（块 0）+ 20（Divider 占位）= 40
        Assert.Equal(40f, result.Floats[0].Rect.Y);
    }

    // T-C3d：锚点索引越界 → 钳到最后一块
    [Fact]
    public void TC3d_AnchorIndexOutOfRange_ClampsToLastBlock()
    {
        var engine = NewEngine();
        Block[] blocks =
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
        };
        var floats = new[] { AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 99, 0, 0) };

        using var result = engine.Layout(blocks, floats, W);

        // 钳到块 1，其首行 Y = 20
        Assert.Equal(20f, result.Floats[0].Rect.Y);
    }

    // T-C3e：全文无文本行盒 → 浮动退化为 Rect 直给路径
    [Fact]
    public void TC3e_NoTextBlocks_FallsBackToRectPath()
    {
        var engine = NewEngine();
        Block[] blocks = { new DividerBlock() };
        var anchored = AnchoredFloat(1, 30, 20, FloatSide.Left, anchorBlock: 0, 0, 0);
        var direct = anchored with { Rect = new LayoutRect(24, 48, 30, 20) };

        using var result = engine.Layout(blocks, new[] { direct }, W);

        // 锚点解析失败 → 保留调用方直给矩形（X=24, Y=48），经 PlaceFloats 归一化不变
        Assert.Equal(24f, result.Floats[0].Rect.X);
        Assert.Equal(48f, result.Floats[0].Rect.Y);
        Assert.Single(result.Lines);   // Divider 占位行盒仍在（不受浮动影响的证明略）
    }
}
