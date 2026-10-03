using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 覆盖式行内样式命令：把 range 内所有 run 的样式强制设为 <see cref="Style"/>（非切换语义）。
/// 用于粘贴时把刚插入文本的样式精确置为源 run 的样式——
/// <see cref="ApplyInlineStyleCommand"/> 的「全启用/全清除」切换语义无法精确置位。
/// </summary>
public sealed record SetInlineStyleCommand(TextRange Range, InlineStyle Style) : IEditCommand
{
    public string Kind => "set-style";

    public EditorState Apply(EditorState state)
    {
        if (Range.IsCollapsed)
        {
            return state;
        }
        var (start, end) = (Range.Start, Range.End);
        var blocks = state.Document.Blocks;
        var newBlocks = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
        }
        for (int i = start.BlockIndex; i <= end.BlockIndex && i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (!BlockTextOps.IsTextBlock(block))
            {
                continue;
            }
            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex ? end.CharIndex : BlockTextOps.GetTextLength(block);
            int count = blockEnd - blockStart;
            if (count <= 0)
            {
                continue;
            }
            newBlocks[i] = BlockTextOps.TransformInlineStyle(block, blockStart, count, _ => Style);
        }
        return new EditorState(state.Document with { Blocks = newBlocks }, state.Selection);
    }
}
