using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 行首 Backspace 的合块命令（Phase 2 设计 §5.1）：把 blockIndex 块合并进上一块。
/// 上一块为文本块时：runs 拼接（保留上一块的块型与块级属性）；上一块为 Divider 时：
/// 删除 Divider，本块上移；上一块为 Image 时：不合并（光标留在本块首，由上层决定行为）。
/// 执行后光标落在合并点（上一块原末尾）。
/// </summary>
public sealed record MergeBlockCommand(int BlockIndex) : IEditCommand
{
    public string Kind => "merge-block";

    public EditorState Apply(EditorState state)
    {
        if (BlockIndex <= 0 || BlockIndex >= state.Document.Blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockIndex),
                $"合块索引 {BlockIndex} 非法（首块无上一块可合）。");
        }

        var blocks = state.Document.Blocks;
        var previous = blocks[BlockIndex - 1];
        var current = blocks[BlockIndex];

        // 上一块是 Divider：删 Divider，本块上移
        if (previous is DividerBlock)
        {
            var kept = new List<Block>(blocks.Count - 1);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i != BlockIndex - 1)
                {
                    kept.Add(blocks[i]);
                }
            }
            return new EditorState(
                state.Document with { Blocks = kept },
                TextRange.Collapse(new TextPosition(BlockIndex - 1, 0)));
        }

        // 上一块非文本（Image）：不合并，光标留在本块首
        if (!BlockTextOps.IsTextBlock(previous))
        {
            return new EditorState(
                state.Document,
                TextRange.Collapse(new TextPosition(BlockIndex, 0)));
        }

        // 上一块是文本：runs 拼接
        if (!BlockTextOps.IsTextBlock(current))
        {
            // 当前块非文本（如 ImageBlock）被 Backspace：删除当前块，光标落到上一块末尾
            var kept2 = new List<Block>(blocks.Count - 1);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i != BlockIndex)
                {
                    kept2.Add(blocks[i]);
                }
            }
            var prevCaret = new TextPosition(BlockIndex - 1, BlockTextOps.GetTextLength(previous));
            return new EditorState(
                state.Document with { Blocks = kept2 },
                TextRange.Collapse(prevCaret));
        }

        var prevRuns = BlockTextOps.GetRuns(previous);
        var currRuns = BlockTextOps.GetRuns(current);
        var merged = new List<TextRun>(prevRuns.Count + currRuns.Count);
        merged.AddRange(prevRuns);
        merged.AddRange(currRuns);
        var mergedBlock = BlockTextOps.WithRuns(previous,
            DeleteRangeCommand.NormalizeRuns(merged));

        var newBlocks = new List<Block>(blocks.Count - 1);
        for (int i = 0; i < BlockIndex - 1; i++)
        {
            newBlocks.Add(blocks[i]);
        }
        newBlocks.Add(mergedBlock);
        for (int i = BlockIndex + 1; i < blocks.Count; i++)
        {
            newBlocks.Add(blocks[i]);
        }

        var caret = new TextPosition(BlockIndex - 1, BlockTextOps.GetTextLength(previous));
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            TextRange.Collapse(caret));
    }
}
