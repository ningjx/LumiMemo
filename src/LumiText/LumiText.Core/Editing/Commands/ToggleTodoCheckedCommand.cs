using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 待办勾选切换命令：复选框点击交互（§0.1 伪待办基线的「点行首切换」）。
/// 不是工具栏命令——由渲染层命中缩进区后调用；勾选态只影响渲染，不影响排版流。
/// </summary>
public sealed record ToggleTodoCheckedCommand(int BlockIndex) : IEditCommand
{
    public string Kind => "toggle-todo-checked";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        if (BlockIndex < 0 || BlockIndex >= blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockIndex));
        }
        if (blocks[BlockIndex] is not TodoBlock t)
        {
            return state; // 目标不是 TodoBlock（点击瞬间文档已变）：不产生变化，不进历史
        }

        var newBlocks = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
        }
        newBlocks[BlockIndex] = new TodoBlock(t.Runs, !t.Checked, t.SpaceAfter);

        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}
