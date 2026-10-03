using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 键入/IME 提交/粘贴的插入命令（Phase 2 设计 §5.1）。
/// 替换当前选区后在坍缩点插入 text；执行后光标落在插入文本末尾。
/// 撤销由 EditorCore 快照通道处理，命令层不提供 Inverse。
/// </summary>
public sealed record InsertTextCommand(string Text) : IEditCommand, InsertTextMarker
{
    public string Kind => "insert";

    public EditorState Apply(EditorState state)
    {
        if (Text.Length == 0)
        {
            return state;
        }

        // 选区非坍缩（无论单块还是跨块）都先删掉，再插入。
        TextPosition insertAt = state.Selection.Start;
        var document = state.Document;
        if (!state.Selection.IsCollapsed)
        {
            var collapsed = new DeleteRangeCommand(state.Selection).Apply(state);
            document = collapsed.Document;
            insertAt = collapsed.Selection.Active;
        }

        var block = document.Blocks[insertAt.BlockIndex];
        if (!BlockTextOps.IsTextBlock(block))
        {
            throw new InvalidOperationException("插入目标不是文本块。");
        }

        var newBlock = BlockTextOps.ReplaceText(block, insertAt.CharIndex, 0, Text);
        var newBlocks = ReplaceBlock(document.Blocks, insertAt.BlockIndex, newBlock);
        var caret = new TextPosition(insertAt.BlockIndex, insertAt.CharIndex + Text.Length);
        return new EditorState(document with { Blocks = newBlocks }, TextRange.Collapse(caret));
    }

    internal static IReadOnlyList<Block> ReplaceBlock(IReadOnlyList<Block> blocks, int index, Block replacement)
    {
        var copy = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            copy[i] = i == index ? replacement : blocks[i];
        }
        return copy;
    }
}
