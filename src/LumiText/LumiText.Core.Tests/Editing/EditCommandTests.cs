using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 命令层单测：InsertText / DeleteRange / SplitBlock / MergeBlock / ApplyInlineStyle 的 Apply 语义。
/// </summary>
public sealed class EditCommandTests
{
    private static EditorState StateWith(params Block[] blocks)
    {
        var doc = new Document(blocks);
        return EditorState.Initial(doc);
    }

    [Fact]
    public void InsertText_CollapsedCaret_InsertsAndMovesCaret()
    {
        var state = StateWith(new ParagraphBlock("hello"))
            with { Selection = TextRange.Collapse(new TextPosition(0, 5)) };
        var after = new InsertTextCommand("!").Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("hello!", p.PlainText);
        Assert.Equal(new TextPosition(0, 6), after.Selection.Active);
    }

    [Fact]
    public void InsertText_AtBlockStart_InsertsAtStartAndAdvancesCaret()
    {
        // 回归（实机 BUG）：光标移到行首打字，字接在末尾、光标停在第一个字符后面。
        var state = StateWith(new ParagraphBlock("123"))
            with { Selection = TextRange.Collapse(new TextPosition(0, 0)) };
        var after = new InsertTextCommand("4").Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("4123", p.PlainText);
        Assert.Equal(new TextPosition(0, 1), after.Selection.Active);
    }

    [Fact]
    public void InsertText_ReplacesSelection()
    {
        var state = StateWith(new ParagraphBlock("hello world"))
            with { Selection = new TextRange(new TextPosition(0, 6), new TextPosition(0, 11)) };
        var after = new InsertTextCommand("there").Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("hello there", p.PlainText);
        Assert.Equal(new TextPosition(0, 11), after.Selection.Active);
    }

    [Fact]
    public void InsertText_AcrossBlocks_CollapsesThenInserts()
    {
        var state = StateWith(
                new ParagraphBlock("aaa"),
                new ParagraphBlock("bbb"),
                new ParagraphBlock("ccc"))
            with { Selection = new TextRange(new TextPosition(0, 1), new TextPosition(2, 1)) };
        var after = new InsertTextCommand("X").Apply(state);
        Assert.Single(after.Document.Blocks);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("aXcc", p.PlainText);
        Assert.Equal(new TextPosition(0, 2), after.Selection.Active);
    }

    [Fact]
    public void DeleteRange_WithinBlock_RemovesText()
    {
        var state = StateWith(new ParagraphBlock("hello"));
        var after = new DeleteRangeCommand(
            new TextRange(new TextPosition(0, 1), new TextPosition(0, 4))).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("ho", p.PlainText);
        Assert.Equal(new TextPosition(0, 1), after.Selection.Active);
    }

    [Fact]
    public void DeleteRange_AcrossTwoBlocks_MergesTailIntoHead()
    {
        var state = StateWith(
            new ParagraphBlock("hello"),
            new ParagraphBlock("world"));
        var after = new DeleteRangeCommand(
            new TextRange(new TextPosition(0, 3), new TextPosition(1, 2))).Apply(state);
        Assert.Single(after.Document.Blocks);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("helrld", p.PlainText);
        Assert.Equal(new TextPosition(0, 3), after.Selection.Active);
    }

    [Fact]
    public void DeleteRange_SpansDivider_RemovesIt()
    {
        var state = StateWith(
            new ParagraphBlock("ab"),
            new DividerBlock(),
            new ParagraphBlock("cd"));
        var after = new DeleteRangeCommand(
            new TextRange(new TextPosition(0, 1), new TextPosition(2, 1))).Apply(state);
        Assert.Single(after.Document.Blocks);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("ad", p.PlainText);
    }

    [Fact]
    public void SplitBlock_MiddleOfParagraph_TwoParagraphs()
    {
        var state = StateWith(new ParagraphBlock("helloworld"));
        var after = new SplitBlockCommand(new TextPosition(0, 5)).Apply(state);
        Assert.Equal(2, after.Document.Blocks.Count);
        var first = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        var second = Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        Assert.Equal("hello", first.PlainText);
        Assert.Equal("world", second.PlainText);
        Assert.Equal(new TextPosition(1, 0), after.Selection.Active);
    }

    [Fact]
    public void SplitBlock_EndOfHeading_NewParagraph()
    {
        var state = StateWith(new HeadingBlock("Title", 1));
        var after = new SplitBlockCommand(new TextPosition(0, 5)).Apply(state);
        Assert.Equal(2, after.Document.Blocks.Count);
        Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
    }

    [Fact]
    public void SplitBlock_TodoBlock_NewTodoUnchecked()
    {
        var state = StateWith(new TodoBlock("task", @checked: true));
        var after = new SplitBlockCommand(new TextPosition(0, 2)).Apply(state);
        var second = Assert.IsType<TodoBlock>(after.Document.Blocks[1]);
        Assert.False(second.Checked); // 新行默认未勾选
    }

    [Fact]
    public void MergeBlock_TwoParagraphs_Concatenates()
    {
        var state = StateWith(
            new ParagraphBlock("hello"),
            new ParagraphBlock("world"));
        var after = new MergeBlockCommand(1).Apply(state);
        Assert.Single(after.Document.Blocks);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal("helloworld", p.PlainText);
        Assert.Equal(new TextPosition(0, 5), after.Selection.Active);
    }

    [Fact]
    public void MergeBlock_PreviousIsDivider_RemovesDivider()
    {
        var state = StateWith(
            new ParagraphBlock("ab"),
            new DividerBlock(),
            new ParagraphBlock("cd"));
        var after = new MergeBlockCommand(2).Apply(state);
        Assert.Equal(2, after.Document.Blocks.Count);
        Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        Assert.Equal(new TextPosition(1, 0), after.Selection.Active);
    }

    [Fact]
    public void ApplyInlineStyle_PartialSelection_TogglesBold()
    {
        var state = StateWith(new ParagraphBlock("hello world"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(0, 5));
        var after = new ApplyInlineStyleCommand(range, InlineStyleFlag.Bold).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Equal(2, p.Runs.Count);
        Assert.True(p.Runs[0].Style!.Bold);
        Assert.Equal("hello", p.Runs[0].Text);
        Assert.Null(p.Runs[1].Style);
    }

    [Fact]
    public void SplitBlock_BulletParagraph_BothHalvesKeepBullet()
    {
        var state = StateWith(new ParagraphBlock("itemone itemtwo", isBullet: true));
        var after = new SplitBlockCommand(new TextPosition(0, 7)).Apply(state);
        Assert.Equal(2, after.Document.Blocks.Count);
        Assert.All(after.Document.Blocks,
            b => Assert.True(Assert.IsType<ParagraphBlock>(b).IsBullet));
    }

    [Fact]
    public void ApplyInlineStyle_AllBold_ClearsIt()
    {
        var state = StateWith(new ParagraphBlock(
            [new TextRun("all", new InlineStyle(Bold: true))]));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(0, 3));
        var after = new ApplyInlineStyleCommand(range, InlineStyleFlag.Bold).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.False(p.Runs[0].Style!.Bold);
    }
}
