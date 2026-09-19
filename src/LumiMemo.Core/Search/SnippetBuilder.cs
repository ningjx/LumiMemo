namespace LumiMemo.Core.Search;

/// <summary>
/// 生成管理器卡片上那一行摘要，并标出命中位置（§12.3）。
/// </summary>
/// <remarks>
/// <para>
/// 只在正文命中时才有摘要——标题或标签命中不需要，因为那两处本来就直接显示在卡片上，
/// 用户一眼就看到了，再摘一段正文反而占地方。
/// </para>
/// <para>
/// 纯函数，没有状态。输入已经是单行（<c>SearchIndex</c> 的纯文本在最后一步把连续空白
/// 压成了单个空格），但这里仍然自己再兜一次换行，因为本方法的契约是"给我一段正文"，
/// 不保证调用方一定传纯文本。
/// </para>
/// </remarks>
public static class SnippetBuilder
{
    /// <summary>命中位置前后各取多少个字符。</summary>
    public const int ContextLength = 40;

    /// <summary>被截断时用的省略号。</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// 生成摘要。正文里找不到查询词、或者查询词为空时，返回空列表。
    /// </summary>
    /// <param name="plainText">便签的正文（最好是纯文本形式）。</param>
    /// <param name="query">查询词。</param>
    /// <returns>
    /// 按顺序排好、可以直接依次渲染成 <c>Run</c> 的片段。
    /// 首个片段可能是省略号，末尾同理；中间最多一段 <see cref="SnippetSegment.IsMatch"/> 为真。
    /// </returns>
    /// <remarks>
    /// 只摘第一处命中。同一张便签里命中很多次时，第一处通常也是最靠近开头的那处，
    /// 把它摘出来足以让用户认出是哪张便签——每处都摘会把一行摘要撑成好几行。
    /// </remarks>
    public static IReadOnlyList<SnippetSegment> Build(string? plainText, string? query)
    {
        var needle = query?.Trim();

        if (string.IsNullOrEmpty(plainText) || string.IsNullOrEmpty(needle))
        {
            return [];
        }

        // 先换成单行再找位置。替换是一对一的（一个换行换一个空格），长度不变，
        // 所以下面算出来的下标对原文同样成立——不会出现"截到一半发现位置偏了"。
        var text = plainText.Replace('\r', ' ').Replace('\n', ' ');

        var position = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        if (position < 0)
        {
            return [];
        }

        var matchEnd = position + needle.Length;
        var start = position - ContextLength;
        var end = matchEnd + ContextLength;

        if (start < 0)
        {
            start = 0;
        }

        if (end > text.Length)
        {
            end = text.Length;
        }

        var segments = new List<SnippetSegment>(4);

        // 命中位置比 ContextLength 更靠近开头时 start 会被夹到 0，此时前面没有内容被截掉，
        // 加省略号会造成"前面还有"的错觉（§12.3 第 5 条）。
        if (start > 0)
        {
            segments.Add(new SnippetSegment(Ellipsis, false));
        }

        if (position > start)
        {
            segments.Add(new SnippetSegment(text[start..position], false));
        }

        segments.Add(new SnippetSegment(text[position..matchEnd], true));

        if (end > matchEnd)
        {
            segments.Add(new SnippetSegment(text[matchEnd..end], false));
        }

        if (end < text.Length)
        {
            segments.Add(new SnippetSegment(Ellipsis, false));
        }

        return segments;
    }
}
