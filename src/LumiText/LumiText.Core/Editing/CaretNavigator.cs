using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.Core.Editing;

/// <summary>
/// 光标导航（Phase 3 M6 §6.4）：纯函数，输入为一次排版产物 + 当前位置，输出目标插入位置。
/// 导航本身不留状态——「期望列（goal-X）」由编辑器按一趟连续上下移动持有并逐次传入。
/// </summary>
public static class CaretNavigator
{
    /// <summary>行组顶缘比较容差（dip）：同一视觉行的多个段 Y 完全相同，半像素足够。</summary>
    private const float RowEpsilon = 0.5f;

    /// <summary>
    /// 上下移动：当前视觉行 → 相邻视觉行，按 <paramref name="goalX"/>（文档坐标）命中目标行内的字符；
    /// 已在首/末行时原地不动（期望列保持）。
    /// </summary>
    /// <remarks>
    /// 视觉行 = 同一 Y 的若干行盒（绕图时一行被挤成图左/图右两段），按行盒列表序（= 视觉序，跨块自然成立）。
    /// 目标行内交给 <see cref="LayoutResult.HitTest"/> 命中：goalX 落在图片缺口里时由空白区兜底
    /// 取水平最近的那一段——与「纵移到最近一段」的常见观感一致。分隔线占位行不进目标候选
    /// （那里放不下光标）。
    /// </remarks>
    public static TextPosition Vertical(LayoutResult layout, TextPosition position, float goalX, bool down)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Lines.Count == 0 || LineFor(layout, position) is not { } current)
        {
            return position;
        }

        // 行组（同 Y 合并、高度取组内最大；Divider 占位行不参与——光标不停在上面）
        var rows = new List<(float Y, float Height)>();
        foreach (var line in layout.Lines)
        {
            if (line.Kind == PlacedLineKind.Divider)
            {
                continue;
            }
            if (rows.Count == 0 || Math.Abs(line.Y - rows[^1].Y) > RowEpsilon)
            {
                rows.Add((line.Y, line.Height));
            }
            else
            {
                var (y, height) = rows[^1];
                rows[^1] = (y, Math.Max(height, line.Height));
            }
        }

        int index = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Y <= current.Y + RowEpsilon)
            {
                index = i;
            }
        }
        int target = down ? index + 1 : index - 1;
        if (index < 0 || target < 0 || target >= rows.Count)
        {
            return position; // 已在首/末行：原地
        }

        var (targetY, targetHeight) = rows[target];
        var hit = layout.HitTest(goalX, targetY + (targetHeight / 2f));
        return hit.Found ? hit.CaretPosition : position;
    }

    /// <summary>
    /// 行首/行末（Home / End）：当前视觉行的首/末插入位置。
    /// 软换行边界上的位置与光标绘制同一归属——算在下一行的行首（见 <see cref="LineFor"/>），
    /// 因此在换行边界按 End 走的是下一行行尾、按 Home 原地不动。
    /// </summary>
    public static TextPosition LineEdge(LayoutResult layout, TextPosition position, bool toEnd)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (LineFor(layout, position) is not { } line)
        {
            return position;
        }
        return new TextPosition(line.BlockIndex,
            toEnd ? line.CharStart + line.CharCount : line.CharStart);
    }

    /// <summary>
    /// 文档首/末（Ctrl+Home / Ctrl+End）：首个/末个<b>可放光标</b>的块（跳过图片块）的首/末插入位置；
    /// 全文没有可放光标的块时回退到文档首。
    /// </summary>
    public static TextPosition DocumentEdge(IReadOnlyList<Block> blocks, bool toEnd)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (toEnd)
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (BlockTextOps.IsTextBlock(blocks[i]))
                {
                    return new TextPosition(i, BlockTextOps.GetTextLength(blocks[i]));
                }
            }
            return new TextPosition(Math.Max(0, blocks.Count - 1), 0);
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            if (BlockTextOps.IsTextBlock(blocks[i]))
            {
                return new TextPosition(i, 0);
            }
        }
        return new TextPosition(0, 0);
    }

    /// <summary>
    /// 位置所在的行盒：块内第一个「行末偏移 &gt; CharIndex」的行盒——块尾与软换行边界都归<b>下一行</b>
    /// （与 <see cref="CaretGeometryCalculator.GetCaret"/> 同一规则，光标画在哪行就算哪行）；
    /// 块内无匹配（空块占位行盒、光标在块尾）时取该块的末行盒。非文本块/越界 → null。
    /// </summary>
    private static PlacedLine? LineFor(LayoutResult layout, TextPosition position)
    {
        PlacedLine? last = null;
        foreach (var line in layout.Lines)
        {
            if (line.BlockIndex != position.BlockIndex)
            {
                continue;
            }
            last = line;
            if (line.CharStart + line.CharCount > position.CharIndex)
            {
                return line;
            }
        }
        return last;
    }
}
