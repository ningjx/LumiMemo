using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 分点切换命令（Phase 2 设计 §5.1）：range 覆盖的段落级 bullet 标记切换。
/// 切换语义与 <see cref="ApplyInlineStyleCommand"/> 对齐：覆盖段落只要有非 bullet 段
/// 就全部启用，否则全部清除。TodoBlock/HeadingBlock 不转（bullet 是 ParagraphBlock 的属性），
/// 非文本块跳过。执行后选区不变。
/// </summary>
public sealed record ToggleBulletCommand(TextRange Range) : IEditCommand
{
    public string Kind => "toggle-bullet";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        var (start, end) = (Range.Start, Range.End);

        bool anyPlain = false;
        for (int i = start.BlockIndex; i <= end.BlockIndex; i++)
        {
            if (blocks[i] is ParagraphBlock { IsBullet: false })
            {
                anyPlain = true;
                break;
            }
        }

        var newBlocks = new Block[blocks.Count];
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
            if (i >= start.BlockIndex && i <= end.BlockIndex && blocks[i] is ParagraphBlock p)
            {
                newBlocks[i] = new ParagraphBlock(p.Runs, p.Style, p.SpaceAfter, anyPlain)
                {
                    Background = p.Background,
                };
                changed = true;
            }
        }

        if (!changed)
        {
            return state; // 覆盖范围内没有 ParagraphBlock——不产生变化，不进历史
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}
