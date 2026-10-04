using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 删除整块命令（Phase 3 收尾）：把 blockIndex 处的**非文本块**（图片 / 分隔线）整块摘掉。
/// 用途是「选中图片按 Delete / Backspace」——选中态只活在编辑器侧（Core 的光标区间表达不了
/// "这张图被选中"），所以由编辑器认领按键、这里做删除。
/// </summary>
/// <remarks>
/// 只删非文本块：文本块的删除有 <see cref="DeleteRangeCommand"/> 与 <see cref="MergeBlockCommand"/>
/// 一整套语义（列表退出、标题降级、runs 拼接……），从这儿开口子会把它们绕过去。
/// 两个边界都按既有约定处理：
/// <list type="bullet">
/// <item>文档不能空——删掉最后一块时补一个空段落，光标有处可落；</item>
/// <item>锚点维护与 <see cref="MergeBlockCommand"/> 的删块路径同一约定：被删块上的锚点并到
/// 前一块首（没有前一块则并到新首块首），其后锚点整块前移。</item>
/// </list>
/// 执行后光标落在**图自己的锚点**上（"图占的那一段"）；没有锚或锚就在被删块上时退回前一块末尾
/// （没有前一块就是新首块首）。
/// </remarks>
public sealed record DeleteBlockCommand(int BlockIndex) : IEditCommand
{
    public string Kind => "delete-block";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        if (BlockIndex < 0 || BlockIndex >= blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockIndex),
                $"删块索引 {BlockIndex} 超出块数 {blocks.Count}。");
        }
        if (BlockTextOps.IsTextBlock(blocks[BlockIndex]))
        {
            throw new InvalidOperationException(
                "DeleteBlockCommand 只删非文本块（图片/分隔线）；文本块请走 DeleteRangeCommand / MergeBlockCommand。");
        }

        var kept = new List<Block>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i != BlockIndex)
            {
                kept.Add(blocks[i]);
            }
        }
        if (kept.Count == 0)
        {
            kept.Add(new ParagraphBlock(string.Empty)); // 文档不能空
        }

        var caret = CaretAfterDelete(blocks, kept, BlockIndex);

        var remapped = FloatAnchors.RemapAll(kept, anchor =>
            anchor.BlockIndex == BlockIndex
                ? new FloatAnchor(Math.Max(0, BlockIndex - 1), 0)
                : anchor.BlockIndex > BlockIndex
                    ? anchor with { BlockIndex = anchor.BlockIndex - 1 }
                    : anchor);

        return new EditorState(
            state.Document with { Blocks = remapped },
            TextRange.Collapse(caret));
    }

    /// <summary>
    /// 删除后的光标落点：**图自己的锚点**——图占的就是"锚字符之后"那一段，删完落回这里最自然
    /// （锚所在块在被删块之后时索引前移一位）。
    /// 没有锚（直给坐标的图）或锚就落在被删块上（自锚）时退回前一块末尾；
    /// 没有前一块就是新首块首。
    /// </summary>
    private static TextPosition CaretAfterDelete(IReadOnlyList<Block> blocks,
        IReadOnlyList<Block> kept, int blockIndex)
    {
        if ((blocks[blockIndex] as ImageBlock)?.Float?.Anchor is { } anchor
            && anchor.BlockIndex != blockIndex)
        {
            int block = Math.Clamp(
                anchor.BlockIndex > blockIndex ? anchor.BlockIndex - 1 : anchor.BlockIndex,
                0, kept.Count - 1);
            return new TextPosition(block,
                Math.Clamp(anchor.CharIndex, 0, BlockTextOps.GetTextLength(kept[block])));
        }

        return blockIndex > 0
            ? new TextPosition(blockIndex - 1, BlockTextOps.GetTextLength(blocks[blockIndex - 1]))
            : new TextPosition(0, 0);
    }
}
