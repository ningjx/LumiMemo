using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 块级背景设置命令（Phase 3 §5）：range 覆盖的文本块统一设为目标底色（null = 清除）。
/// 非文本块（Divider/Image，无行盒）跳过；全部块已是目标值时不产生变化（不进历史）。
/// 执行后选区不变。
/// </summary>
public sealed record SetBlockBackgroundCommand(TextRange Range, Color32? Background) : IEditCommand
{
    public string Kind => "set-background";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        var (start, end) = (Range.Start, Range.End);

        var newBlocks = new Block[blocks.Count];
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
            if (i < start.BlockIndex || i > end.BlockIndex || !BlockTextOps.IsTextBlock(blocks[i]))
            {
                continue;
            }
            if (blocks[i].Background == Background)
            {
                continue;
            }
            newBlocks[i] = blocks[i] with { Background = Background };
            changed = true;
        }

        if (!changed)
        {
            return state;
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}
