using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 伪待办切换命令（Phase 2 设计 §5.1）：range 覆盖的段落 ↔ TodoBlock 互转。
/// 切换语义：覆盖块里只要有非 TodoBlock 的文本块就全部转为 Todo（未勾选），
/// 否则全部转回普通段落（bullet 标记随之清除）。非文本块跳过。执行后选区不变。
/// </summary>
public sealed record ToggleTodoCommand(TextRange Range) : IEditCommand
{
    public string Kind => "toggle-todo";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        var (start, end) = (Range.Start, Range.End);

        bool anyNonTodo = false;
        for (int i = start.BlockIndex; i <= end.BlockIndex; i++)
        {
            if (blocks[i] is not TodoBlock && BlockTextOps.IsTextBlock(blocks[i]))
            {
                anyNonTodo = true;
                break;
            }
        }

        var newBlocks = new Block[blocks.Count];
        bool changed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = blocks[i];
            if (i < start.BlockIndex || i > end.BlockIndex)
            {
                continue;
            }
            switch (blocks[i])
            {
                case TodoBlock t when !anyNonTodo:
                    // 全已是 Todo → 转回普通段落（SpaceAfter/底色保留，bullet 归位 false）
                    newBlocks[i] = new ParagraphBlock(t.Runs, null, t.SpaceAfter)
                    {
                        Background = t.Background,
                    };
                    changed = true;
                    break;
                case ParagraphBlock p when anyNonTodo:
                    newBlocks[i] = new TodoBlock(p.Runs, false, p.SpaceAfter)
                    {
                        Background = p.Background,
                    };
                    changed = true;
                    break;
                case HeadingBlock h when anyNonTodo:
                    newBlocks[i] = new TodoBlock(h.Runs, false, h.SpaceAfter)
                    {
                        Background = h.Background,
                    };
                    changed = true;
                    break;
                // Divider/Image/已合目标的块：不动
            }
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
