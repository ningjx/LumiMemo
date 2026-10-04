using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 行内字号比命令（"把选中的文字放大成标题那么大"）：把 range 内所有 run 的
/// <see cref="InlineStyle.FontSizeRatio"/> 设成 <paramref name="Ratio"/>（null = 清除），
/// 其余行内样式（粗斜下划/颜色/底色）原样保留。
/// </summary>
/// <remarks>
/// <para>
/// <b>切换语义与底色一致</b>：区间内**全部已是该比例**就清除（回到段落字号），否则整段设成该比例——
/// 工具栏上再点一次同一档就是取消。
/// </para>
/// <para>
/// 字号比是**相对段落字号**的倍数，布局与渲染侧早就在用（<c>Win2DTextMeasurer</c> 按
/// <c>base × ratio</c> 设字号，行盒也随之被撑高）；这条命令只负责"怎么把它写进文档"。
/// </para>
/// <para>选区坍缩时不产生变化（没有文字可放大），与底色同一条纪律。</para>
/// </remarks>
public sealed record SetInlineFontSizeRatioCommand(TextRange Range, float? Ratio) : IEditCommand
{
    public string Kind => "set-inline-font-size-ratio";

    /// <summary>比例比较的容差：比例是算出来的（22f / 14f），手改过的 JSON 可能只差最后几位。</summary>
    private const float RatioEpsilon = 0.0001f;

    public EditorState Apply(EditorState state)
    {
        if (Ratio is { } value && value <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Ratio), value, "字号比必须是正数。");
        }

        if (Range.IsCollapsed)
        {
            return state;
        }

        var (start, end) = (Range.Start, Range.End);
        var blocks = state.Document.Blocks;

        // 区间内全都已经是这个比例 → 这一下当"取消"（回到段落字号）
        float? target = Ratio is { } ratio && AllAtRatio(blocks, start, end, ratio) ? null : Ratio;

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
            if (count <= 0 || !HasChange(blocks[i], blockStart, count, target))
            {
                continue;
            }

            newBlocks[i] = BlockTextOps.TransformInlineStyle(blocks[i], blockStart, count,
                style => style is null
                    ? target is null ? null : new InlineStyle(FontSizeRatio: target)
                    : style with { FontSizeRatio = target });
            changed = true;
        }

        if (!changed)
        {
            return state; // 区间内字号比没变：不进历史
        }

        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }

    /// <summary>区间内是否**全部**已经带这个比例（是＝这一下是取消）。一个字符都没有时返回 false。</summary>
    private static bool AllAtRatio(
        IReadOnlyList<Block> blocks, TextPosition start, TextPosition end, float ratio)
    {
        bool any = false;
        for (int i = start.BlockIndex; i <= end.BlockIndex && i < blocks.Count; i++)
        {
            if (!BlockTextOps.IsTextBlock(blocks[i]))
            {
                continue;
            }

            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex ? end.CharIndex : BlockTextOps.GetTextLength(blocks[i]);
            if (blockEnd - blockStart <= 0)
            {
                continue;
            }

            foreach (var run in BlockTextOps.SliceRuns(blocks[i], blockStart, blockEnd - blockStart))
            {
                any = true;
                float? current = run.Style?.FontSizeRatio;
                if (current is null || Math.Abs(current.Value - ratio) > RatioEpsilon)
                {
                    return false;
                }
            }
        }

        return any;
    }

    /// <summary>区间内是否真的会变（避免「同一档再点一次」却什么都没写就进撤销栈）。</summary>
    private static bool HasChange(Block block, int start, int count, float? ratio)
    {
        foreach (var run in BlockTextOps.SliceRuns(block, start, count))
        {
            float? current = run.Style?.FontSizeRatio;
            bool same = current is null
                ? ratio is null
                : ratio is { } target && Math.Abs(current.Value - target) <= RatioEpsilon;
            if (!same)
            {
                return true;
            }
        }

        return false;
    }
}
