using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// Phase 3 M2：标题语义（SetHeadingLevelCommand 的 toggle 批量语义、标记丢弃、底色保留）
/// 与 SplitBlock 的「标题内回车 → 新块为正文段」。
/// </summary>
public sealed class HeadingCommandTests
{
    private static readonly Color32 Bg = new(0x40, 0xFF, 0xD9, 0x66);

    private static EditorState StateWith(params Block[] blocks) =>
        EditorState.Initial(new Document(blocks));

    private static TextRange Caret(int blockIndex, int charIndex = 1) =>
        TextRange.Collapse(new TextPosition(blockIndex, charIndex));

    // ---------------- SetHeadingLevelCommand ----------------

    [Fact]
    public void SetHeading_Paragraph_BecomesHeading()
    {
        var state = StateWith(new ParagraphBlock("标题"));
        var after = new SetHeadingLevelCommand(Caret(0), 2).Apply(state);
        var h = Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        Assert.Equal(2, h.Level);
        Assert.Equal("标题", h.PlainText);
    }

    [Fact]
    public void SetHeading_SameHeadingLevel_TogglesBackToParagraph()
    {
        var state = StateWith(new HeadingBlock("标题", 2));
        var after = new SetHeadingLevelCommand(Caret(0), 2).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.False(p.IsBullet);
        Assert.Equal("标题", p.PlainText);
    }

    [Fact]
    public void SetHeading_DifferentHeadingLevel_SwitchesLevel()
    {
        var state = StateWith(new HeadingBlock("标题", 1));
        var after = new SetHeadingLevelCommand(Caret(0), 2).Apply(state);
        Assert.Equal(2, Assert.IsType<HeadingBlock>(after.Document.Blocks[0]).Level);
    }

    [Fact]
    public void SetHeading_MixedRange_AllBecomeTargetLevel()
    {
        var state = StateWith(
            new ParagraphBlock("p"),
            new HeadingBlock("h2", 2),
            new ParagraphBlock("q"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(2, 1));
        var after = new SetHeadingLevelCommand(range, 2).Apply(state);
        Assert.All(after.Document.Blocks, b => Assert.Equal(2, Assert.IsType<HeadingBlock>(b).Level));
    }

    [Fact]
    public void SetHeading_AllAtTargetLevel_TogglesAllBackToParagraph()
    {
        var state = StateWith(new HeadingBlock("a", 3), new HeadingBlock("b", 3));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(1, 1));
        var after = new SetHeadingLevelCommand(range, 3).Apply(state);
        Assert.All(after.Document.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void SetHeading_TodoBlock_MarkersDropped()
    {
        var state = StateWith(new TodoBlock("任务", @checked: true));
        var after = new SetHeadingLevelCommand(Caret(0), 1).Apply(state);
        var h = Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        Assert.Equal("任务", h.PlainText);

        // 回到正文：不是待办，也不留 bullet 标记
        var back = new SetHeadingLevelCommand(Caret(0), 0).Apply(after);
        var p = Assert.IsType<ParagraphBlock>(back.Document.Blocks[0]);
        Assert.False(p.IsBullet);
    }

    [Fact]
    public void SetHeading_BulletParagraph_MarkerDropped()
    {
        var state = StateWith(new ParagraphBlock("项", isBullet: true));
        var after = new SetHeadingLevelCommand(Caret(0), 1).Apply(state);
        Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
    }

    [Fact]
    public void SetHeading_NonTextBlocks_Skipped()
    {
        var state = StateWith(new DividerBlock(), new ParagraphBlock("p"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(1, 1));
        var after = new SetHeadingLevelCommand(range, 1).Apply(state);
        Assert.IsType<DividerBlock>(after.Document.Blocks[0]);
        Assert.IsType<HeadingBlock>(after.Document.Blocks[1]);
    }

    [Fact]
    public void SetHeading_AlreadyParagraph_TargetParagraph_NoChange()
    {
        var state = StateWith(new ParagraphBlock("p"));
        var after = new SetHeadingLevelCommand(Caret(0), 0).Apply(state);
        Assert.Same(state.Document, after.Document); // 无变化不进历史
    }

    [Fact]
    public void SetHeading_PreservesSpaceAfterAndSelection()
    {
        var state = StateWith(new ParagraphBlock("标题", spaceAfter: 6f))
            with { Selection = Caret(0) };
        var after = new SetHeadingLevelCommand(state.Selection, 1).Apply(state);
        var h = Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        Assert.Equal(6f, h.SpaceAfter);
        Assert.Equal(state.Selection, after.Selection);
    }

    [Fact]
    public void SetHeading_InvalidLevel_Throws()
    {
        var state = StateWith(new ParagraphBlock("p"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SetHeadingLevelCommand(Caret(0), 4).Apply(state));
    }

    // ---------------- SplitBlock：标题内回车 ----------------

    [Fact]
    public void SplitBlock_MiddleOfHeading_NewParagraph()
    {
        var state = StateWith(new HeadingBlock("HelloWorld", 1));
        var after = new SplitBlockCommand(new TextPosition(0, 5)).Apply(state);
        var first = Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        var second = Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        Assert.Equal("Hello", first.PlainText);
        Assert.Equal("World", second.PlainText);
    }

    // ---------------- 标题预设（加粗 / 段前距） ----------------

    [Fact]
    public void HeadingPresets_BoldAndSpaceBefore()
    {
        Assert.True(new HeadingBlock("t", 1).EffectiveStyle.Bold);
        Assert.True(new HeadingBlock("t", 2).EffectiveStyle.Bold);
        Assert.True(new HeadingBlock("t", 3).EffectiveStyle.Bold);
        Assert.False(TextStyle.Default.Bold);

        Assert.Equal(12f, new HeadingBlock("t", 1).SpaceBefore);
        Assert.Equal(10f, new HeadingBlock("t", 2).SpaceBefore);
        Assert.Equal(8f, new HeadingBlock("t", 3).SpaceBefore);
        Assert.Equal(22f, new HeadingBlock("t", 1).EffectiveStyle.FontSize);
    }
}
