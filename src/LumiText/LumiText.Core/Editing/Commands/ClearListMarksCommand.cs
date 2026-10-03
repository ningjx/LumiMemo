using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 清除列表标记命令（Phase 3 打磨）：把 range 覆盖的段落的 bullet 标记去掉、<see cref="TodoBlock"/>
/// 转回普通段落。
/// </summary>
/// <remarks>
/// 与 <see cref="ToggleBulletCommand"/> / <see cref="ToggleTodoCommand"/> 的区别：<b>只清不加</b>。
/// Toggle 的语义是「覆盖段里只要有非该标记的段就全部启用」，选区里混有带标记与不带的段时会把
/// 不带的也标上——Esc 要的是「退出」，故单独一条命令。
/// </remarks>
public sealed record ClearListMarksCommand(TextRange Range) : IEditCommand
{
    public string Kind => "clear-list-marks";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        var (start, end) = (Range.Start, Range.End);

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
                case ParagraphBlock { IsBullet: true } p:
                    newBlocks[i] = new ParagraphBlock(p.Runs, p.Style, p.SpaceAfter, isBullet: false);
                    changed = true;
                    break;
                case TodoBlock t:
                    // 转回普通段落：勾选态丢弃，runs/间距保留（与 ToggleTodoCommand 的清除分支一致）
                    newBlocks[i] = new ParagraphBlock(t.Runs, null, t.SpaceAfter);
                    changed = true;
                    break;
            }
        }

        if (!changed)
        {
            return state; // 选区里没有分点/勾选：不产生变化，不进历史
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}
