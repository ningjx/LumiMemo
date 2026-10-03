using LumiText.Core.Documents;

namespace LumiText.Core.Editing;

/// <summary>
/// 浮动锚点的维护（Phase 3 M4）：图片锚在「某个字符之后」，而文字编辑会让锚点的
/// (块索引, 块内偏移) 相对那个字漂移——<b>分块/合块/删块/插图</b>（块结构变化）与
/// <b>块内增删文字</b>都必须在命令里显式重映射，锚点才真的"随文字移动"。
/// 各命令按自己的变化构造映射（本类只做遍历与写回）。
/// </summary>
public static class FloatAnchors
{
    /// <summary>对块列表里所有图片块的锚点做重映射（非图片块、无锚点块原样返回）。</summary>
    public static IReadOnlyList<Block> RemapAll(IReadOnlyList<Block> blocks,
        Func<FloatAnchor, FloatAnchor> map)
    {
        var result = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            result[i] = Remap(blocks[i], map);
        }
        return result;
    }

    /// <summary>单块锚点重映射（非图片块或无锚点原样返回）。</summary>
    public static Block Remap(Block block, Func<FloatAnchor, FloatAnchor> map)
    {
        if (block is ImageBlock image && image.Float?.Anchor is { } anchor)
        {
            var mapped = map(anchor);
            if (mapped != anchor)
            {
                return image with { Float = image.Float with { Anchor = mapped } };
            }
        }
        return block;
    }

    /// <summary>块内文本增删后的锚点重映射：atChar 及之后的锚点偏移 delta（可负）。</summary>
    public static FloatAnchor ShiftCharIndex(FloatAnchor anchor, int blockIndex, int atChar, int delta) =>
        anchor.BlockIndex == blockIndex && anchor.CharIndex >= atChar
            ? anchor with { CharIndex = Math.Max(0, anchor.CharIndex + delta) }
            : anchor;
}
