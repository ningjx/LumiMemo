using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 文字底色命令（Phase 3 打磨，取代原「块级底色」）：把 range 内所有 run 的行内背景设为
/// <paramref name="Background"/>（null = 清除），其余行内样式（粗斜下划/颜色/字号）原样保留。
/// 选区坍缩时不产生变化（没有文字可上色）。
/// </summary>
public sealed record SetInlineBackgroundCommand(TextRange Range, Color32? Background) : IEditCommand
{
    public string Kind => "set-inline-background";

    public EditorState Apply(EditorState state)
    {
        if (Range.IsCollapsed)
        {
            return state;
        }
        var (start, end) = (Range.Start, Range.End);
        var blocks = state.Document.Blocks;
        var newBlocks = new Block[blocks.Count];
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
            if (i < start.BlockIndex || i > end.BlockIndex || !BlockTextOps.IsTextBlock(blocks[i]))
            {
                continue;
            }
            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex ? end.CharIndex : BlockTextOps.GetTextLength(blocks[i]);
            int count = blockEnd - blockStart;
            if (count <= 0 || !HasChange(blocks[i], blockStart, count, Background))
            {
                continue;
            }
            newBlocks[i] = BlockTextOps.TransformInlineStyle(blocks[i], blockStart, count,
                style => style is null
                    ? Background is null ? null : new InlineStyle(Background: Background)
                    : style with { Background = Background });
            changed = true;
        }

        if (!changed)
        {
            return state; // 区间内底色没变：不进历史
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }

    /// <summary>区间内是否真的会变（避免「同色再设一次」进撤销栈）。</summary>
    private static bool HasChange(Block block, int start, int count, Color32? background)
    {
        foreach (var run in BlockTextOps.SliceRuns(block, start, count))
        {
            Color32? current = run.Style?.Background;
            if (current != background)
            {
                return true;
            }
        }
        return false;
    }
}
