namespace LumiMemo.Core.Search;

/// <summary>
/// 从正文里裁出一段「命中点附近的摘要」，并按命中位置切好片段（§12.3）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>纯函数。</strong>输入正文与关键词，输出可以逐段渲染的切片；
/// 高亮的颜色与字体是展示层的事，本层只回答「哪几段是命中」。
/// </para>
/// <para>
/// 窗口策略：以<strong>第一个命中</strong>为锚，向前保留一段上下文，再取到目标长度；
/// 命中点靠近文本尾部时窗口往回补足，避免只显示一个尾巴。两端被裁掉的地方各加一个
/// 「…」段。没有任何命中（关键词全在标题/标签，或本来就没有查询词）时，
/// 退化为「从开头取一段」，行为与旧版预览一致。
/// </para>
/// </remarks>
public static class SnippetBuilder
{
    /// <summary>
    /// 摘要的目标长度（字符）。
    /// </summary>
    /// <remarks>
    /// 贴着列表项两行的可见容量取（约 2×26 个汉字）；留一点富余是因为
    /// 中英混排时窄字符能多塞几个。取太长没有意义：超出的部分永远被两行截断，
    /// 还会把命中点推到可见区之外——用户看到的就是「搜到了却看不到词在哪」。
    /// </remarks>
    public const int DefaultMaxLength = 56;

    /// <summary>
    /// 命中点前面保留的上下文长度（字符）。
    /// </summary>
    /// <remarks>
    /// 取得小是有意的：命中点要落在可见区靠前的位置，后面的上下文才有空间展示。
    /// 取 30+ 会把命中推到第二行之后、甚至两行截断区之外。
    /// </remarks>
    public const int DefaultLeadContext = 16;

    /// <summary>截断处的省略号。</summary>
    public const string Ellipsis = "…";

    /// <summary>构建摘要切片。</summary>
    /// <param name="text">正文（纯文本）。</param>
    /// <param name="terms">关键词（已经分好词；空集合表示没有查询，退化为开头预览）。</param>
    /// <param name="maxLength">摘要目标长度。</param>
    /// <param name="leadContext">命中点前保留的上下文长度。</param>
    public static IReadOnlyList<SnippetSegment> Build(
        string? text,
        IReadOnlyList<string> terms,
        int maxLength = DefaultMaxLength,
        int leadContext = DefaultLeadContext)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(leadContext);

        string[] needles = [.. terms.Where(static term => !string.IsNullOrEmpty(term))];

        int firstHit = FirstHitIndex(text, needles);

        int windowStart;
        int windowEnd;

        if (firstHit < 0)
        {
            windowStart = 0;
            windowEnd = System.Math.Min(text.Length, maxLength);
        }
        else
        {
            windowStart = System.Math.Max(0, firstHit - leadContext);
            windowEnd = System.Math.Min(text.Length, windowStart + maxLength);

            // 文本快到头、窗口尾部没占满时往回补足：宁可多留一点前面的上下文，
            // 也不要在能显示全的时候只显示一个尾巴。
            if (windowEnd == text.Length)
            {
                windowStart = System.Math.Max(0, windowEnd - maxLength);
            }
        }

        IReadOnlyList<(int Start, int Length)> matches = FindMatches(text, needles, windowStart, windowEnd);

        var result = new List<SnippetSegment>();

        if (windowStart > 0)
        {
            result.Add(new SnippetSegment(Ellipsis, false));
        }

        var cursor = windowStart;

        foreach ((int start, int length) in matches)
        {
            if (start > cursor)
            {
                result.Add(new SnippetSegment(text[cursor..start], false));
            }

            result.Add(new SnippetSegment(text[start..(start + length)], true));
            cursor = start + length;
        }

        if (cursor < windowEnd)
        {
            result.Add(new SnippetSegment(text[cursor..windowEnd], false));
        }

        if (windowEnd < text.Length)
        {
            result.Add(new SnippetSegment(Ellipsis, false));
        }

        return result;
    }

    private static int FirstHitIndex(string text, IReadOnlyList<string> needles)
    {
        var first = -1;

        foreach (string needle in needles)
        {
            int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

            if (index >= 0 && (first < 0 || index < first))
            {
                first = index;
            }
        }

        return first;
    }

    /// <summary>
    /// 窗口内的全部命中区间：不重叠、按起点排序、已合并相交项、已裁剪到窗口边界。
    /// </summary>
    private static IReadOnlyList<(int Start, int Length)> FindMatches(
        string text, IReadOnlyList<string> needles, int windowStart, int windowEnd)
    {
        var raw = new List<(int Start, int Length)>();

        foreach (string needle in needles)
        {
            var index = 0;

            while (index < text.Length)
            {
                int found = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);

                if (found < 0)
                {
                    break;
                }

                // 只保留与窗口有交集的命中（长词可以从窗口左边界之前开始）。
                if (found < windowEnd && found + needle.Length > windowStart)
                {
                    raw.Add((found, needle.Length));
                }

                index = found + needle.Length;
            }
        }

        raw.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int Length)>();

        foreach ((int start, int length) in raw)
        {
            if (merged.Count > 0)
            {
                (int lastStart, int lastLength) = merged[^1];

                if (start <= lastStart + lastLength)
                {
                    int end = System.Math.Max(lastStart + lastLength, start + length);
                    merged[^1] = (lastStart, end - lastStart);

                    continue;
                }
            }

            merged.Add((start, length));
        }

        return
        [
            .. merged
                .Select(range => (
                    Start: System.Math.Max(range.Start, windowStart),
                    End: System.Math.Min(range.Start + range.Length, windowEnd)))
                .Where(static range => range.End > range.Start)
                .Select(static range => (range.Start, range.End - range.Start)),
        ];
    }
}
