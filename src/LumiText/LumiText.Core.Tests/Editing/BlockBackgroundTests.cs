using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// Phase 3 M1：块级背景命令（§5）与「重建块保留底色」回归（§3.4 陷阱清单）。
/// </summary>
public sealed class BlockBackgroundTests
{
    private static readonly Color32 Highlight = new(0x40, 0xFF, 0xD9, 0x66);
    private static readonly Color32 Other = new(0x40, 0x66, 0xAE, 0xE0);

    private static EditorState StateWith(params Block[] blocks) =>
        EditorState.Initial(new Document(blocks));

    // ---------------- SetBlockBackgroundCommand ----------------

    [Fact]
    public void Set_AppliesToCoveredTextBlocks()
    {
        var state = StateWith(new ParagraphBlock("a"), new ParagraphBlock("b"), new ParagraphBlock("c"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(1, 1));
        var after = new SetBlockBackgroundCommand(range, Highlight).Apply(state);

        Assert.Equal(Highlight, after.Document.Blocks[0].Background);
        Assert.Equal(Highlight, after.Document.Blocks[1].Background);
        Assert.Null(after.Document.Blocks[2].Background);
        Assert.Equal(state.Selection, after.Selection);
    }

    [Fact]
    public void Set_Null_ClearsBackground()
    {
        var state = StateWith(new ParagraphBlock("a") { Background = Highlight });
        var after = new SetBlockBackgroundCommand(
            TextRange.Collapse(new TextPosition(0, 0)), null).Apply(state);
        Assert.Null(after.Document.Blocks[0].Background);
    }

    [Fact]
    public void Set_OverwritesExistingColor()
    {
        var state = StateWith(new ParagraphBlock("a") { Background = Highlight });
        var after = new SetBlockBackgroundCommand(
            TextRange.Collapse(new TextPosition(0, 0)), Other).Apply(state);
        Assert.Equal(Other, after.Document.Blocks[0].Background);
    }

    [Fact]
    public void Set_NonTextBlocks_Skipped()
    {
        var state = StateWith(new ParagraphBlock("a"), new DividerBlock(), new ParagraphBlock("b"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(2, 1));
        var after = new SetBlockBackgroundCommand(range, Highlight).Apply(state);

        Assert.Equal(Highlight, after.Document.Blocks[0].Background);
        Assert.Null(after.Document.Blocks[1].Background); // Divider 无行盒，跳过
        Assert.Equal(Highlight, after.Document.Blocks[2].Background);
    }

    [Fact]
    public void Set_AlreadyTargetValue_NoChange()
    {
        var state = StateWith(new ParagraphBlock("a") { Background = Highlight });
        var after = new SetBlockBackgroundCommand(
            TextRange.Collapse(new TextPosition(0, 0)), Highlight).Apply(state);
        Assert.Same(state.Document, after.Document); // 无变化不进历史
    }

    // ---------------- 重建块保留底色（§3.4 回归）----------------

    private static readonly Color32 Bg = Highlight;

    private static void AssertBackgroundsKept(IReadOnlyList<Block> blocks, params Color32?[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], blocks[i].Background);
        }
    }

    [Fact]
    public void InsertText_PreservesBackground()
    {
        var state = StateWith(new ParagraphBlock("ab") { Background = Bg })
            with { Selection = TextRange.Collapse(new TextPosition(0, 1)) };
        var after = new InsertTextCommand("X").Apply(state);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void DeleteRange_PreservesBackground()
    {
        var state = StateWith(new ParagraphBlock("abcd") { Background = Bg });
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(0, 3));
        var after = new DeleteRangeCommand(range).Apply(state);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void ApplyInlineStyle_PreservesBackground()
    {
        var state = StateWith(new ParagraphBlock("abcd") { Background = Bg });
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(0, 4));
        var after = new ApplyInlineStyleCommand(range, InlineStyleFlag.Bold).Apply(state);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void SplitBlock_PreservesBackgroundOnBothHalves()
    {
        var state = StateWith(new ParagraphBlock("abcd") { Background = Bg });
        var after = new SplitBlockCommand(new TextPosition(0, 2)).Apply(state);
        AssertBackgroundsKept(after.Document.Blocks, Bg, Bg);
    }

    [Fact]
    public void SplitHeadingAtEnd_TailParagraphKeepsBackground()
    {
        var state = StateWith(new HeadingBlock("ab", 1) { Background = Bg });
        var after = new SplitBlockCommand(new TextPosition(0, 2)).Apply(state);
        Assert.IsType<HeadingBlock>(after.Document.Blocks[0]);
        Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        AssertBackgroundsKept(after.Document.Blocks, Bg, Bg);
    }

    [Fact]
    public void MergeBlock_KeepsPreviousBlockBackground()
    {
        var state = StateWith(
            new ParagraphBlock("ab") { Background = Bg },
            new ParagraphBlock("cd"));
        var after = new MergeBlockCommand(1).Apply(state);
        Assert.Single(after.Document.Blocks);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void ToggleBullet_PreservesBackground()
    {
        var state = StateWith(new ParagraphBlock("ab") { Background = Bg });
        var after = new ToggleBulletCommand(
            TextRange.Collapse(new TextPosition(0, 1))).Apply(state);
        Assert.True(Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]).IsBullet);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void ToggleTodo_PreservesBackgroundBothWays()
    {
        var state = StateWith(new ParagraphBlock("ab") { Background = Bg });
        var range = TextRange.Collapse(new TextPosition(0, 1));
        var toTodo = new ToggleTodoCommand(range).Apply(state);
        Assert.IsType<TodoBlock>(toTodo.Document.Blocks[0]);
        AssertBackgroundsKept(toTodo.Document.Blocks, Bg);

        var back = new ToggleTodoCommand(range).Apply(toTodo);
        Assert.IsType<ParagraphBlock>(back.Document.Blocks[0]);
        AssertBackgroundsKept(back.Document.Blocks, Bg);
    }

    [Fact]
    public void ToggleTodoChecked_PreservesBackground()
    {
        var state = StateWith(new TodoBlock("ab") { Background = Bg });
        var after = new ToggleTodoCheckedCommand(0).Apply(state);
        Assert.True(Assert.IsType<TodoBlock>(after.Document.Blocks[0]).Checked);
        AssertBackgroundsKept(after.Document.Blocks, Bg);
    }

    [Fact]
    public void Undo_RestoresBackground()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("ab")]));
        var range = TextRange.Collapse(new TextPosition(0, 1));
        core.ApplyCommand(new SetBlockBackgroundCommand(range, Bg));
        Assert.Equal(Bg, core.Document.Blocks[0].Background);

        Assert.True(core.Undo());
        Assert.Null(core.Document.Blocks[0].Background);
        Assert.True(core.Redo());
        Assert.Equal(Bg, core.Document.Blocks[0].Background);
    }
}
