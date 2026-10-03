using LumiText.Core.Documents;

namespace LumiText.Core.Editing;

/// <summary>
/// 块文本的读取与 run 序列重写（命令实现与测试的公共底座）。
/// 所有方法都不修改原 record，返回新实例——沿用「不可变 Document + with 替换」语义。
/// </summary>
public static class BlockTextOps
{
    /// <summary>块内字符容量（TextPosition.CharIndex 的合法上界）。非文本块为 0。</summary>
    public static int GetTextLength(Block block) => block switch
    {
        ParagraphBlock p => p.PlainText.Length,
        HeadingBlock h => h.PlainText.Length,
        TodoBlock t => t.PlainText.Length,
        _ => 0,
    };

    /// <summary>块的纯文本。非文本块返回空串。</summary>
    public static string GetPlainText(Block block) => block switch
    {
        ParagraphBlock p => p.PlainText,
        HeadingBlock h => h.PlainText,
        TodoBlock t => t.PlainText,
        _ => string.Empty,
    };

    /// <summary>块是否承载文本（能作为光标/选区的落点）。</summary>
    public static bool IsTextBlock(Block block) =>
        block is ParagraphBlock or HeadingBlock or TodoBlock;

    /// <summary>取块的 run 序列（非文本块抛 <see cref="InvalidOperationException"/>）。</summary>
    public static IReadOnlyList<TextRun> GetRuns(Block block) => block switch
    {
        ParagraphBlock p => p.Runs,
        HeadingBlock h => h.Runs,
        TodoBlock t => t.Runs,
        _ => throw new InvalidOperationException($"{block.GetType().Name} 不承载文本 run。"),
    };

    /// <summary>用新 run 序列重建同型块（保留块级属性：层级/勾选态/样式/间距/bullet 标记/底色）。</summary>
    public static Block WithRuns(Block block, IReadOnlyList<TextRun> runs) => block switch
    {
        ParagraphBlock p => new ParagraphBlock(runs, p.Style, p.SpaceAfter, p.IsBullet)
        {
            Background = p.Background,
        },
        HeadingBlock h => new HeadingBlock(runs, h.Level, h.SpaceAfter)
        {
            Background = h.Background,
        },
        TodoBlock t => new TodoBlock(runs, t.Checked, t.SpaceAfter)
        {
            Background = t.Background,
        },
        _ => throw new InvalidOperationException($"{block.GetType().Name} 不承载文本 run。"),
    };

    /// <summary>
    /// 把块内 [start, start+count) 的文本区间替换为 replacement，按样式边界重切 run。
    /// 插入文本继承插入点前字符的样式；块首插入继承首 run 样式；空块用 null 样式。
    /// </summary>
    public static Block ReplaceText(Block block, int start, int count, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Contains('\r') || replacement.Contains('\n'))
        {
            throw new ArgumentException("块内文本不允许含换行符（换行即分段）。", nameof(replacement));
        }

        int length = GetTextLength(block);
        if (start < 0 || start > length || count < 0 || start + count > length)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"区间 [{start}, {start + count}) 超出块文本长度 {length}。");
        }

        var runs = GetRuns(block);
        var newRuns = new List<TextRun>(runs.Count + 2);
        bool inserted = false;
        int runStart = 0;

        foreach (var run in runs)
        {
            int runEnd = runStart + run.Text.Length;
            int keepHead = Math.Clamp(start - runStart, 0, run.Text.Length);            // 区间之前的保留长度
            int keepTailStart = Math.Clamp(start + count - runStart, 0, run.Text.Length); // 区间之后的保留起点

            bool isBeforeEdit = runEnd <= start;      // 本 run 完全在编辑区间之前
            bool isAfterEdit = runStart >= start + count; // 本 run 完全在编辑区间之后
            bool boundaryInsertAtRightEdge = isBeforeEdit && runEnd == start && replacement.Length > 0;

            if (isBeforeEdit || isAfterEdit)
            {
                // 与编辑区间无交：整段保留；唯一的插入时机是「插入点恰好落在本 run 右缘」
                AppendRun(newRuns, run.Text, run.Style);
                if (!inserted && boundaryInsertAtRightEdge)
                {
                    AppendRun(newRuns, replacement, run.Style);
                    inserted = true;
                }
            }
            else
            {
                // 与编辑区间有交（或插入点落在本 run 内部）：切头 → 插 replacement → 切尾
                if (keepHead > 0)
                {
                    AppendRun(newRuns, run.Text[..keepHead], run.Style);
                }
                if (!inserted && replacement.Length > 0)
                {
                    // 插入样式：优先取「插入点前字符」的样式（keepHead > 0 即本 run 内有前字符）；
                    // 否则本 run 就是插入点所在 run，同样继承本 run。
                    AppendRun(newRuns, replacement, run.Style);
                    inserted = true;
                }
                if (keepTailStart < run.Text.Length)
                {
                    AppendRun(newRuns, run.Text[keepTailStart..], run.Style);
                }
            }
            runStart = runEnd;
        }

        // 空块（runs 为空）的兜底插入：无样式可继承，用 null
        if (!inserted && replacement.Length > 0)
        {
            AppendRun(newRuns, replacement, null);
        }

        return WithRuns(block, newRuns);
    }

    /// <summary>
    /// 对块内 [start, start+count) 区间做行内样式变换（transform 收到旧样式返回新样式）。
    /// 区间外 run 原样保留；区间内 run 按区间边界切分后逐段应用 transform。
    /// </summary>
    public static Block TransformInlineStyle(Block block, int start, int count,
        Func<InlineStyle?, InlineStyle?> transform)
    {
        int length = GetTextLength(block);
        if (count < 0 || start < 0 || start + count > length)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"区间 [{start}, {start + count}) 超出块文本长度 {length}。");
        }
        if (count == 0)
        {
            return block;
        }

        var runs = GetRuns(block);
        var newRuns = new List<TextRun>(runs.Count + 2);
        int runStart = 0;
        foreach (var run in runs)
        {
            int runEnd = runStart + run.Text.Length;
            if (runEnd <= start || runStart >= start + count)
            {
                AppendRun(newRuns, run.Text, run.Style);
            }
            else
            {
                int head = Math.Max(0, start - runStart);
                int tail = Math.Min(run.Text.Length, start + count - runStart);
                if (head > 0)
                {
                    AppendRun(newRuns, run.Text[..head], run.Style);
                }
                if (tail > head)
                {
                    AppendRun(newRuns, run.Text[head..tail], transform(run.Style));
                }
                if (tail < run.Text.Length)
                {
                    AppendRun(newRuns, run.Text[tail..], run.Style);
                }
            }
            runStart = runEnd;
        }
        return WithRuns(block, newRuns);
    }

    /// <summary>
    /// 读取块内 [start, start+count) 区间所有 run 的「样式片段」（供命令记录旧样式）。
    /// 返回片段 = (文本, 样式) 对序列，拼接文本恰为区间内容。
    /// </summary>
    public static IReadOnlyList<TextRun> SliceRuns(Block block, int start, int count)
    {
        int length = GetTextLength(block);
        if (count < 0 || start < 0 || start + count > length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        var result = new List<TextRun>();
        if (count == 0)
        {
            return result;
        }
        int runStart = 0;
        foreach (var run in GetRuns(block))
        {
            int runEnd = runStart + run.Text.Length;
            int head = Math.Max(runStart, start);
            int tail = Math.Min(runEnd, start + count);
            if (head < tail)
            {
                result.Add(new TextRun(run.Text[(head - runStart)..(tail - runStart)], run.Style));
            }
            runStart = runEnd;
            if (runStart >= start + count)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>追加 run 到列表：空文本丢弃；与末 run 样式相同则合并（避免无意义的碎 run）。</summary>
    private static void AppendRun(List<TextRun> runs, string text, InlineStyle? style)
    {
        if (text.Length == 0)
        {
            return;
        }
        if (runs.Count > 0 && EqualityComparer<InlineStyle?>.Default.Equals(runs[^1].Style, style))
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        }
        else
        {
            runs.Add(new TextRun(text, style));
        }
    }
}
