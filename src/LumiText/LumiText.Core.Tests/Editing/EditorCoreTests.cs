using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// EditorCore + UndoStack 单测：命令总线、事件、快照式撤销/重做、命令合并。
/// </summary>
public sealed class EditorCoreTests
{
    private static EditorCore CoreWith(params Block[] blocks) => new(new Document(blocks));

    [Fact]
    public void ApplyCommand_RaisesDocumentChanged()
    {
        var core = CoreWith(new ParagraphBlock("a"));
        int fired = 0;
        core.DocumentChanged += (_, _) => fired++;
        core.ApplyCommand(new InsertTextCommand("b"));
        Assert.Equal(1, fired);
        Assert.Equal("ab", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
    }

    [Fact]
    public void ApplyCommand_NoOpCommand_DoesNotPushHistory()
    {
        var core = CoreWith(new ParagraphBlock("a"));
        core.ApplyCommand(new InsertTextCommand(string.Empty));
        Assert.Equal(0, core.History.UndoCount);
    }

    [Fact]
    public void Undo_RestoresDocumentAndSelection()
    {
        var core = CoreWith(new ParagraphBlock("ab"));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertTextCommand("cd"));
        Assert.Equal("abcd", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
        Assert.True(core.Undo());
        Assert.Equal("ab", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
        Assert.Equal(new TextPosition(0, 2), core.Selection.Active);
    }

    [Fact]
    public void Redo_ReappliesCommand()
    {
        var core = CoreWith(new ParagraphBlock("ab"));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertTextCommand("cd"));
        core.Undo();
        Assert.True(core.Redo());
        Assert.Equal("abcd", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
        Assert.Equal(new TextPosition(0, 4), core.Selection.Active);
    }

    [Fact]
    public void UndoDeleteRange_RestoresDeletedContent()
    {
        var core = CoreWith(
            new ParagraphBlock("hello"),
            new ParagraphBlock("world"));
        core.SetSelection(new TextRange(new TextPosition(0, 3), new TextPosition(1, 2)));
        core.ApplyCommand(new DeleteRangeCommand(core.Selection));
        Assert.Single(core.Document.Blocks);
        Assert.True(core.Undo());
        Assert.Equal(2, core.Document.Blocks.Count);
        Assert.Equal("hello", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
        Assert.Equal("world", ((ParagraphBlock)core.Document.Blocks[1]).PlainText);
    }

    [Fact]
    public void ConsecutiveInserts_MergeIntoOneHistoryEntry()
    {
        var core = CoreWith(new ParagraphBlock(""));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 0)));
        core.ApplyCommand(new InsertTextCommand("a"));
        core.ApplyCommand(new InsertTextCommand("b"));
        core.ApplyCommand(new InsertTextCommand("c"));
        Assert.Equal(1, core.History.UndoCount); // 合并为一条
        core.Undo();
        Assert.Equal("", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
    }

    [Fact]
    public void InsertAfterSelectionJump_DoesNotMerge()
    {
        var core = CoreWith(new ParagraphBlock("xy"));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertTextCommand("a"));
        // 选区跳变（用户点了别处）
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 0)));
        core.ApplyCommand(new InsertTextCommand("b"));
        Assert.Equal(2, core.History.UndoCount);
    }

    [Fact]
    public void UndoStack_CapacityOverflow_DropsOldest()
    {
        var core = CoreWith(new ParagraphBlock(""));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 0)));
        // 用 SetSelection 打断合并，确保每条都是独立历史
        for (int i = 0; i < UndoStack.Capacity + 10; i++)
        {
            core.ApplyCommand(new InsertTextCommand("x"));
            core.SetSelection(TextRange.Collapse(new TextPosition(0, i + 1)));
            // SetSelection 到同一位置不改变 Selection（Active 相同），需要真的跳变
            core.SetSelection(TextRange.Collapse(new TextPosition(0, i)));
            core.SetSelection(TextRange.Collapse(new TextPosition(0, i + 1)));
        }
        Assert.True(core.History.UndoCount <= UndoStack.Capacity);
    }

    [Fact]
    public void SetSelection_RaisesSelectionChangedOnly()
    {
        var core = CoreWith(new ParagraphBlock("a"));
        int docFired = 0, selFired = 0;
        core.DocumentChanged += (_, _) => docFired++;
        core.SelectionChanged += (_, _) => selFired++;
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 1)));
        Assert.Equal(0, docFired);
        Assert.Equal(1, selFired);
    }

    [Fact]
    public void GetPlainText_TodoBlockCarriesPrefix()
    {
        var core = CoreWith(
            new ParagraphBlock("normal"),
            new TodoBlock("done", @checked: true),
            new TodoBlock("pending", @checked: false));
        Assert.Equal("normal\n☑ done\n☐ pending", core.GetPlainText());
    }

    [Fact]
    public void ResetDocument_ClearsHistory()
    {
        var core = CoreWith(new ParagraphBlock("a"));
        core.ApplyCommand(new InsertTextCommand("b"));
        core.ResetDocument(new Document([new ParagraphBlock("new")]));
        Assert.Equal(0, core.History.UndoCount);
        Assert.Equal("new", ((ParagraphBlock)core.Document.Blocks[0]).PlainText);
    }
}
