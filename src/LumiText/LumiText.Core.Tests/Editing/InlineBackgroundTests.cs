using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// Phase 3 打磨：文字底色（行内背景）命令——只作用于选中文字，其余行内样式原样保留。
/// </summary>
public sealed class InlineBackgroundTests
{
    private static readonly Color32 Highlight = new(0x66, 0xFF, 0xD9, 0x66);
    private static readonly Color32 Other = new(0x66, 0x66, 0xD9, 0xFF);

    private static EditorState StateWith(params Block[] blocks) =>
        EditorState.Initial(new Document(blocks));

    private static TextRange Range(int block, int start, int end) =>
        new(new TextPosition(block, start), new TextPosition(block, end));

    [Fact]
    public void Set_ColorsOnlySelectedRange()
    {
        var state = StateWith(new ParagraphBlock("abcdef"));
        var after = new SetInlineBackgroundCommand(Range(0, 2, 4), Highlight).Apply(state);

        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Null(p.Runs[0].Style?.Background);                       // "ab"
        Assert.Equal(Highlight, p.Runs[1].Style!.Background);           // "cd"
        Assert.Null(p.Runs[2].Style?.Background);                       // "ef"
        Assert.Equal("abcdef", p.PlainText);
    }

    [Fact]
    public void Set_Null_ClearsBackground()
    {
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(Background: Highlight)),
            new TextRun("cd"),
        ]));
        var after = new SetInlineBackgroundCommand(Range(0, 0, 2), null).Apply(state);

        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Null(p.Runs[0].Style?.Background);
        Assert.Equal("abcd", p.PlainText);
    }

    [Fact]
    public void Set_KeepsOtherInlineStyles()
    {
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(Bold: true, Italic: true, Color: Other)),
        ]));
        var after = new SetInlineBackgroundCommand(Range(0, 0, 2), Highlight).Apply(state);

        var style = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]).Runs[0].Style!;
        Assert.Equal(Highlight, style.Background);
        Assert.True(style.Bold);      // 其余样式原样保留
        Assert.True(style.Italic);
        Assert.Equal(Other, style.Color);
    }

    [Fact]
    public void Set_AcrossBlocks_CoversRangeOnly()
    {
        var state = StateWith(
            new ParagraphBlock("abc"),
            new ParagraphBlock("def"));
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(1, 2));
        var after = new SetInlineBackgroundCommand(range, Highlight).Apply(state);

        var first = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        var second = Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        Assert.Null(first.Runs[0].Style?.Background);                     // "a"
        Assert.Equal(Highlight, first.Runs[1].Style!.Background);         // "bc"
        Assert.Equal(Highlight, second.Runs[0].Style!.Background);        // "de"
        Assert.Null(second.Runs[1].Style?.Background);                    // "f"
    }

    [Fact]
    public void Set_SameColor_NoChange()
    {
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(Background: Highlight)),
        ]));
        var after = new SetInlineBackgroundCommand(Range(0, 0, 2), Highlight).Apply(state);
        Assert.Same(state.Document, after.Document); // 同色再设一次：不进历史
    }

    [Fact]
    public void Set_CollapsedRange_NoChange()
    {
        var state = StateWith(new ParagraphBlock("abc"));
        var after = new SetInlineBackgroundCommand(
            TextRange.Collapse(new TextPosition(0, 1)), Highlight).Apply(state);
        Assert.Same(state.Document, after.Document); // 没有选中文字可上色
    }

    [Fact]
    public void Set_UndoRestoresBackground()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("abcd")]));
        core.SetSelection(Range(0, 1, 3));
        core.ApplyCommand(new SetInlineBackgroundCommand(core.Selection, Highlight));

        var runs = Assert.IsType<ParagraphBlock>(core.Document.Blocks[0]).Runs;
        Assert.Equal(Highlight, runs[1].Style!.Background); // "bc" 被切出来带底色
        Assert.Equal("abcd", string.Concat(runs.Select(r => r.Text)));

        Assert.True(core.Undo());
        var back = Assert.IsType<ParagraphBlock>(core.Document.Blocks[0]).Runs;
        Assert.All(back, r => Assert.Null(r.Style?.Background));
    }

    [Fact]
    public void Set_ThenInsertText_TypedTextInheritsBackground()
    {
        // 底色随样式传播：在带底色的字后输入，新字继承插入点前字符的样式
        var core = new EditorCore(new Document([new ParagraphBlock([
            new TextRun("ab", new InlineStyle(Background: Highlight)),
        ])]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertTextCommand("c"));

        var runs = Assert.IsType<ParagraphBlock>(core.Document.Blocks[0]).Runs;
        Assert.Equal("abc", string.Concat(runs.Select(r => r.Text)));
        Assert.Equal(Highlight, runs[^1].Style!.Background);
    }
}
