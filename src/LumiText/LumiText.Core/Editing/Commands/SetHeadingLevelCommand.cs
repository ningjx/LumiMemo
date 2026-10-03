using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 标题级别设置命令（Phase 3 设计 §5）：range 覆盖的文本块统一转换到目标级别。
/// <paramref name="Level"/>：0 = 正文段，1–3 = H1–H3。
/// 切换语义与 <see cref="ToggleBulletCommand"/> 对齐：范围内只要有「不是目标级别」的文本块
/// 就全部转为目标级别；全部已是目标级别则全部回正文（工具栏按钮的 toggle 语义）。
/// 转标题时丢弃 bullet/todo 标记（标题无缩进标记）；非文本块跳过；无变化不进历史。
/// </summary>
public sealed record SetHeadingLevelCommand(TextRange Range, int Level) : IEditCommand
{
    public string Kind => "set-heading-level";

    public EditorState Apply(EditorState state)
    {
        if (Level is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(Level), Level, "标题级别只支持 0（正文）–3。");
        }

        var blocks = state.Document.Blocks;
        var (start, end) = (Range.Start, Range.End);

        bool anyOtherLevel = false;
        for (int i = start.BlockIndex; i <= end.BlockIndex && i < blocks.Count; i++)
        {
            if (BlockTextOps.IsTextBlock(blocks[i]) && !IsAtLevel(blocks[i], Level))
            {
                anyOtherLevel = true;
                break;
            }
        }

        // 全已是目标级别 → 回正文（按钮 toggle）；否则 → 统一设为目标级别
        int target = anyOtherLevel ? Level : 0;

        var newBlocks = new Block[blocks.Count];
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
            if (i < start.BlockIndex || i > end.BlockIndex || !BlockTextOps.IsTextBlock(blocks[i]))
            {
                continue;
            }
            var converted = Convert(blocks[i], target);
            if (converted != blocks[i])
            {
                newBlocks[i] = converted;
                changed = true;
            }
        }

        if (!changed)
        {
            return state; // 无变化不进历史（如已是正文且目标也是正文）
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }

    private static bool IsAtLevel(Block block, int level) => level switch
    {
        0 => block is ParagraphBlock { IsBullet: false },
        _ => block is HeadingBlock h && h.Level == level,
    };

    private static Block Convert(Block block, int level)
    {
        if (IsAtLevel(block, level))
        {
            return block;
        }
        return level switch
        {
            0 => block switch
            {
                // 转正文：bullet/todo 标记丢弃（保留 runs/间距/底色）
                ParagraphBlock p => new ParagraphBlock(p.Runs, p.Style, p.SpaceAfter)
                {
                    Background = p.Background,
                },
                HeadingBlock h => new ParagraphBlock(h.Runs, null, h.SpaceAfter)
                {
                    Background = h.Background,
                },
                TodoBlock t => new ParagraphBlock(t.Runs, null, t.SpaceAfter)
                {
                    Background = t.Background,
                },
                _ => block,
            },
            _ => block switch
            {
                ParagraphBlock p => new HeadingBlock(p.Runs, level, p.SpaceAfter)
                {
                    Background = p.Background,
                },
                HeadingBlock h => new HeadingBlock(h.Runs, level, h.SpaceAfter)
                {
                    Background = h.Background,
                },
                TodoBlock t => new HeadingBlock(t.Runs, level, t.SpaceAfter)
                {
                    Background = t.Background,
                },
                _ => block,
            },
        };
    }
}
