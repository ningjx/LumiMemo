using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 块结构切换命令单测（Phase 2 §5.1）：ToggleBullet / ToggleTodo / ToggleTodoChecked。
/// </summary>
public sealed class ToggleCommandTests
{
    private static EditorState StateWith(params Block[] blocks)
    {
        var doc = new Document(blocks);
        return EditorState.Initial(doc);
    }

    // ---------------- ToggleBullet ----------------

    [Fact]
    public void ToggleBullet_PlainParagraph_BecomesBullet()
    {
        var state = StateWith(new ParagraphBlock("item"));
        var range = TextRange.Collapse(new TextPosition(0, 2));
        var after = new ToggleBulletCommand(range).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.True(p.IsBullet);
        Assert.Equal("item", p.PlainText);
    }

    [Fact]
    public void ToggleBullet_BulletParagraph_BecomesPlain()
    {
        var state = StateWith(new ParagraphBlock("item", isBullet: true));
        var range = TextRange.Collapse(new TextPosition(0, 2));
        var after = new ToggleBulletCommand(range).Apply(state);
        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.False(p.IsBullet);
    }

    [Fact]
    public void ToggleBullet_MixedRange_AllBecomeBullet()
    {
        var state = StateWith(
            new ParagraphBlock("a", isBullet: true),
            new ParagraphBlock("b"),
            new ParagraphBlock("c"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(2, 1));
        var after = new ToggleBulletCommand(range).Apply(state);
        // 范围内只要有非 bullet 段 → 全部启用
        foreach (var block in after.Document.Blocks)
        {
            Assert.True(Assert.IsType<ParagraphBlock>(block).IsBullet);
        }
    }

    [Fact]
    public void ToggleBullet_TodoAndHeading_NotTouched()
    {
        var state = StateWith(
            new TodoBlock("task"),
            new HeadingBlock("title", 1),
            new ParagraphBlock("p"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(2, 1));
        var after = new ToggleBulletCommand(range).Apply(state);
        Assert.IsType<TodoBlock>(after.Document.Blocks[0]);
        Assert.IsType<HeadingBlock>(after.Document.Blocks[1]);
        Assert.True(Assert.IsType<ParagraphBlock>(after.Document.Blocks[2]).IsBullet);
    }

    [Fact]
    public void ToggleBullet_OnlyNonParagraphBlocks_NoChange()
    {
        var state = StateWith(new TodoBlock("task"), new DividerBlock());
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(1, 0));
        var after = new ToggleBulletCommand(range).Apply(state);
        Assert.Same(state.Document, after.Document); // 无 ParagraphBlock：不产生变化
    }

    // ---------------- ToggleTodo ----------------

    [Fact]
    public void ToggleTodo_PlainParagraph_BecomesTodo()
    {
        var state = StateWith(new ParagraphBlock("item"));
        var range = TextRange.Collapse(new TextPosition(0, 2));
        var after = new ToggleTodoCommand(range).Apply(state);
        var t = Assert.IsType<TodoBlock>(after.Document.Blocks[0]);
        Assert.False(t.Checked);
        Assert.Equal("item", t.PlainText);
    }

    [Fact]
    public void ToggleTodo_BulletParagraph_BecomesTodo_BulletCleared()
    {
        var state = StateWith(new ParagraphBlock("item", isBullet: true));
        var range = TextRange.Collapse(new TextPosition(0, 2));
        var after = new ToggleTodoCommand(range).Apply(state);
        Assert.IsType<TodoBlock>(after.Document.Blocks[0]);
        // 转回段落时 bullet 归位（ToggleTodo 往返不留 bullet 残留）
        var back = new ToggleTodoCommand(TextRange.Collapse(new TextPosition(0, 2))).Apply(after);
        var p = Assert.IsType<ParagraphBlock>(back.Document.Blocks[0]);
        Assert.False(p.IsBullet);
    }

    [Fact]
    public void ToggleTodo_AllTodo_BackToParagraph()
    {
        var state = StateWith(
            new TodoBlock("a", @checked: true),
            new TodoBlock("b"));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(1, 1));
        var after = new ToggleTodoCommand(range).Apply(state);
        Assert.All(after.Document.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void ToggleTodo_MixedRange_AllBecomeTodo()
    {
        var state = StateWith(
            new TodoBlock("a"),
            new ParagraphBlock("b"),
            new HeadingBlock("c", 1));
        var range = new TextRange(new TextPosition(0, 0), new TextPosition(2, 1));
        var after = new ToggleTodoCommand(range).Apply(state);
        Assert.All(after.Document.Blocks, b => Assert.IsType<TodoBlock>(b));
    }

    // ---------------- ToggleTodoChecked ----------------

    [Fact]
    public void ToggleTodoChecked_TodoBlock_FlipsChecked()
    {
        var state = StateWith(new TodoBlock("task"));
        var after = new ToggleTodoCheckedCommand(0).Apply(state);
        Assert.True(Assert.IsType<TodoBlock>(after.Document.Blocks[0]).Checked);
        var back = new ToggleTodoCheckedCommand(0).Apply(after);
        Assert.False(Assert.IsType<TodoBlock>(back.Document.Blocks[0]).Checked);
    }

    [Fact]
    public void ToggleTodoChecked_NotTodo_NoChange()
    {
        var state = StateWith(new ParagraphBlock("p"));
        var after = new ToggleTodoCheckedCommand(0).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    [Fact]
    public void ToggleTodoChecked_PreservesRunsAndSelection()
    {
        var state = StateWith(new TodoBlock(
                [new TextRun("a", new InlineStyle(Bold: true)), new TextRun("b")]))
            with { Selection = TextRange.Collapse(new TextPosition(0, 1)) };
        var after = new ToggleTodoCheckedCommand(0).Apply(state);
        var t = Assert.IsType<TodoBlock>(after.Document.Blocks[0]);
        Assert.Equal(2, t.Runs.Count);
        Assert.True(t.Runs[0].Style!.Bold);
        Assert.Equal(state.Selection, after.Selection);
    }

    // ---------------- 排版属性联动 ----------------

    [Fact]
    public void ParagraphBlock_Bullet_IndentFollowsFlag()
    {
        var plain = new ParagraphBlock("x");
        var bullet = new ParagraphBlock("x", isBullet: true);
        Assert.Equal(0f, plain.LeftIndent);
        Assert.True(bullet.LeftIndent > 0f);
    }
}
