using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 行内样式切换命令（Phase 2 设计 §5.1）：对 range 内所有 run 应用样式变换。
/// 变换是「切换语义」：选区内只要有一处未启用目标样式就全部启用，否则全部清除
/// （与 Word/RichEdit 的 Ctrl+B 行为对齐）。执行后选区不变（保持高亮供继续切换其他样式）。
/// </summary>
public sealed record ApplyInlineStyleCommand(
    TextRange Range,
    InlineStyleFlag Flag) : IEditCommand
{
    public string Kind => "style";

    public EditorState Apply(EditorState state)
    {
        if (Range.IsCollapsed)
        {
            return state; // 坍缩选区不做事（RichEdit 对空选区的 Ctrl+B 也只影响后续键入——Phase 2 简化）
        }

        var start = Range.Start;
        var end = Range.End;
        var blocks = state.Document.Blocks;

        // 先采样：选区内是否「全部已启用」目标样式
        bool allEnabled = SampleAllEnabled(blocks, start, end, Flag);

        var newBlocks = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
        }

        for (int i = start.BlockIndex; i <= end.BlockIndex; i++)
        {
            var block = blocks[i];
            if (!BlockTextOps.IsTextBlock(block))
            {
                continue; // Divider/Image 跳过
            }
            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex
                ? end.CharIndex
                : BlockTextOps.GetTextLength(block);
            int count = blockEnd - blockStart;
            if (count <= 0)
            {
                continue;
            }
            newBlocks[i] = BlockTextOps.TransformInlineStyle(block, blockStart, count,
                style => Toggle(style, Flag, enable: !allEnabled));
        }

        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }

    private static bool SampleAllEnabled(IReadOnlyList<Block> blocks,
        TextPosition start, TextPosition end, InlineStyleFlag flag)
    {
        for (int i = start.BlockIndex; i <= end.BlockIndex; i++)
        {
            var block = blocks[i];
            if (!BlockTextOps.IsTextBlock(block))
            {
                continue;
            }
            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex
                ? end.CharIndex
                : BlockTextOps.GetTextLength(block);
            if (blockEnd <= blockStart)
            {
                continue;
            }
            foreach (var run in BlockTextOps.SliceRuns(block, blockStart, blockEnd - blockStart))
            {
                if (!IsEnabled(run.Style, flag))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsEnabled(InlineStyle? style, InlineStyleFlag flag) => flag switch
    {
        InlineStyleFlag.Bold => style?.Bold == true,
        InlineStyleFlag.Italic => style?.Italic == true,
        InlineStyleFlag.Strikethrough => style?.Strikethrough == true,
        InlineStyleFlag.Underline => style?.Underline == true,
        _ => false,
    };

    private static InlineStyle Toggle(InlineStyle? style, InlineStyleFlag flag, bool enable)
    {
        var s = style ?? new InlineStyle();
        return flag switch
        {
            InlineStyleFlag.Bold => s with { Bold = enable },
            InlineStyleFlag.Italic => s with { Italic = enable },
            InlineStyleFlag.Strikethrough => s with { Strikethrough = enable },
            InlineStyleFlag.Underline => s with { Underline = enable },
            _ => s,
        };
    }
}

/// <summary>可切换的行内样式位（对应工具栏按钮与快捷键）。</summary>
public enum InlineStyleFlag
{
    Bold,
    Italic,
    Strikethrough,
    Underline,
}
