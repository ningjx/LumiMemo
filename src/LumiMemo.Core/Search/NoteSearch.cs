using LumiMemo.Core.Models;

namespace LumiMemo.Core.Search;

/// <summary>
/// 搜索的匹配与排序（§12.1、§12.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>纯函数，不进 DI、没有状态。</strong>便签列表由调用方传快照进来；
/// 纯文本正文直接取 <see cref="Note.Content"/>（.lumi 的 <c>text</c> 投影就是它），
/// 不再有「另一份索引文本」的概念。
/// </para>
/// <para>
/// <strong>匹配模型：按空白/标点切词 + 每词子串匹配 + AND。</strong>
/// 用户用空格标注词边界（「会议 记录」要求两个词都出现）；没有空格时整段就是一个词，
/// 与单关键词搜索完全同款。不做词库级的中文分词：对子串匹配来说，显式的词边界
/// 比猜词更准，也不会把「会议记录」猜成「会议 / 记录」两段而改变命中语义。
/// </para>
/// </remarks>
public static class NoteSearch
{
    // ---- 基础分（§12.2 表一）。同一个便签多处命中时取最高的一条，不相加。----

    /// <summary>标题完全等于查询。</summary>
    public const double TitleExactScore = 1000;

    /// <summary>标题以查询开头。</summary>
    public const double TitlePrefixScore = 800;

    /// <summary>标题包含查询。</summary>
    public const double TitleContainsScore = 600;

    /// <summary>标签完全等于查询。</summary>
    public const double TagExactScore = 500;

    /// <summary>标签包含查询。</summary>
    public const double TagContainsScore = 400;

    /// <summary>正文包含查询。</summary>
    public const double BodyContainsScore = 200;

    // ---- 修正项（§12.2 表二）----

    /// <summary>正文命中落在开头时基础分的倍数。</summary>
    public const double BodyLeadMultiplier = 1.5;

    /// <summary>"开头"的宽度，按字符计。</summary>
    public const int BodyLeadLength = 100;

    /// <summary>正文里每多出现一次的加分。</summary>
    public const double PerOccurrenceBonus = 10;

    /// <summary>出现次数加分最多计几次。超过部分不再加分，免得一份刷屏的长文压过一切。</summary>
    public const int MaxCountedOccurrences = 10;

    /// <summary>置顶的便签。</summary>
    public const double TopMostBonus = 50;

    /// <summary>七天内改过。</summary>
    public const double RecentWeekBonus = 30;

    /// <summary>三十天内改过。</summary>
    public const double RecentMonthBonus = 10;

    /// <summary>正文为空。空便签不太可能是搜索目标。</summary>
    public const double EmptyBodyPenalty = -100;

    /// <summary>
    /// 「最近」的窗口，<see cref="RecentWeekBonus"/> 与管理器过滤条上那个「最近 7 天」共用。
    /// </summary>
    /// <remarks>
    /// 两处各写一个 <c>TimeSpan.FromDays(7)</c> 的话，把窗口改成 3 天时只会改到一处，
    /// 而症状是「搜出来的结果比筛出来的多」——两者都叫「最近」却没有同一套口径，
    /// 用户没有任何办法理解那多出来的几条是怎么来的。
    /// </remarks>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// 把用户输入切成关键词：空白（含全角空格）与中英文逗号、顿号、分号都是分隔符。
    /// </summary>
    public static string[] SplitTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(
                [' ', '\t', '\r', '\n', '　', ',', '，', '、', ';', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 按查询词筛出命中的便签并排好序（§12.1、§12.2）。
    /// </summary>
    /// <param name="notes">候选便签，通常是窗口管理器手里的当前列表。</param>
    /// <param name="query">用户输入的查询词；空白查询返回空列表，见下。</param>
    /// <param name="topMostIds">当前处于置顶的便签 id，用于 <see cref="TopMostBonus"/>。可为 <c>null</c>。</param>
    /// <param name="now">用来算"最近改过"的当前时刻。显式传入是为了让用例不受真实时钟影响。</param>
    /// <remarks>
    /// <para>
    /// <strong>空查询返回空列表，不是"匹配到全部"。</strong>管理器的列表在查询词为空时
    /// 压根不走这里，而是走 <see cref="OrderForList"/> 直接展示（§12.1、§15.8）。
    /// 这条区分很重要：若让空查询返回全部，那些"分数很低但确实匹配"的规则
    /// 会把整个列表重排一遍，用户会在清空输入框的瞬间看到列表乱跳。
    /// </para>
    /// <para>
    /// <strong>匹配用 <see cref="StringComparison.OrdinalIgnoreCase"/> 的子串查找。</strong>
    /// 中文没有大小写问题，英文按字节序忽略大小写即可。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SearchHit> Search(
        IReadOnlyList<Note> notes,
        string? query,
        IReadOnlySet<Guid>? topMostIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notes);

        string[] terms = SplitTerms(query);

        if (terms.Length == 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();

        foreach (Note note in notes)
        {
            SearchHit? hit = MatchNote(note, terms, topMostIds, now);

            if (hit is not null)
            {
                hits.Add(hit);
            }
        }

        return [.. hits.OrderByDescending(static h => h.Score)
                       .ThenByDescending(static h => h.Note.UpdatedAt)
                       .ThenBy(static h => h.Note.Id)];
    }

    /// <summary>
    /// 查询词为空时列表的展示顺序：修改时间倒序（§15.8）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Search"/> 的最终排序键<strong>是同一套</strong>（时间倒序、再按 id 升序）。
    /// 两条路径若各写一份，同一批数据在"输入查询词"与"清空查询词"之间会出现不一致的顺序，
    /// 用户会以为搜索把数据弄乱了。
    /// </remarks>
    public static IReadOnlyList<Note> OrderForList(IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        return [.. notes.OrderByDescending(static n => n.UpdatedAt).ThenBy(static n => n.Id)];
    }

    /// <summary>
    /// 判断一张便签是否命中全部关键词（AND）并算出总分；有词未命中则返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <strong>分数结构：每个词的得分相加 + 整篇级别的修正项只加一次。</strong>
    /// 词分内部（基础分取最高、开头倍数、次数加分）与单关键词时代完全一致；
    /// 多词时「每个词都命中」本身就是强信号，相加让全词命中的排在只命中一个词的前面。
    /// </remarks>
    private static SearchHit? MatchNote(
        Note note,
        string[] terms,
        IReadOnlySet<Guid>? topMostIds,
        DateTimeOffset now)
    {
        var plain = note.Content ?? string.Empty;

        var total = 0.0;
        var bestTermScore = double.NegativeInfinity;
        var titlePosition = -1;
        var bodyPosition = -1;

        foreach (string term in terms)
        {
            (bool any, double score, int termTitle, int termBody) = EvaluateTerm(note, plain, term);

            if (!any)
            {
                return null;
            }

            total += score;

            if (score > bestTermScore)
            {
                bestTermScore = score;

                // 位置信息取自「贡献最大的词」的命中处（展示高亮用）。
                titlePosition = termTitle;
                bodyPosition = termBody;
            }
        }

        // 命中的标签取各词并集，保持标签本来的顺序。
        var matchedTags = new List<string>();

        foreach (string tag in note.Tags)
        {
            foreach (string term in terms)
            {
                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchedTags.Add(tag);

                    break;
                }
            }
        }

        if (topMostIds?.Contains(note.Id) == true)
        {
            total += TopMostBonus;
        }

        // 未来时间戳（时钟回拨、外部写了个超前的时间）也会落进"七天内"这一档，
        // 不给它单开一个分支：加 30 分与加 0 分的差别不值得多一条规则。
        var age = now - note.UpdatedAt;

        if (age <= RecentWindow)
        {
            total += RecentWeekBonus;
        }
        else if (age <= TimeSpan.FromDays(30))
        {
            total += RecentMonthBonus;
        }

        if (string.IsNullOrWhiteSpace(plain))
        {
            total += EmptyBodyPenalty;
        }

        return new SearchHit(note, titlePosition, matchedTags, bodyPosition, total);
    }

    /// <summary>一个关键词在便签上的命中情况与得分。</summary>
    /// <remarks>
    /// 分数结构与单关键词时代的一致：六条基础分取最高（不相加），
    /// 正文命中在开头处乘倍数（只乘正文那一档内部），最后加该词的出现次数加分。
    /// </remarks>
    private static (bool Any, double Score, int TitlePosition, int BodyPosition) EvaluateTerm(
        Note note, string plain, string term)
    {
        int titlePosition = note.Title.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        int bodyPosition = plain.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        bool tagHit = false;

        foreach (string tag in note.Tags)
        {
            if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                tagHit = true;

                break;
            }
        }

        if (titlePosition < 0 && bodyPosition < 0 && !tagHit)
        {
            return (false, 0, -1, -1);
        }

        var best = 0.0;

        if (titlePosition >= 0)
        {
            best = KeepHigher(best, TitleScore(note.Title, term, titlePosition));
        }

        if (tagHit)
        {
            foreach (string tag in note.Tags)
            {
                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    best = KeepHigher(best, TagScore(tag, term));
                }
            }
        }

        if (bodyPosition >= 0)
        {
            var bodyScore = BodyContainsScore;

            if (bodyPosition < BodyLeadLength)
            {
                bodyScore *= BodyLeadMultiplier;
            }

            best = KeepHigher(best, bodyScore);
        }

        var occurrences = CountOccurrences(plain, term);
        if (occurrences > MaxCountedOccurrences)
        {
            occurrences = MaxCountedOccurrences;
        }

        best += occurrences * PerOccurrenceBonus;

        return (true, best, titlePosition, bodyPosition);
    }

    /// <summary>标题那一档的分：位置为 0 才是"开头"，等长才是"完全等于"。</summary>
    private static double TitleScore(string title, string needle, int position)
    {
        if (position != 0)
        {
            return TitleContainsScore;
        }

        return title.Length == needle.Length ? TitleExactScore : TitlePrefixScore;
    }

    /// <summary>
    /// 标签那一档的分。传进来的标签<strong>已经确认包含查询词</strong>，
    /// 所以等长就是"完全等于"。
    /// </summary>
    private static double TagScore(string tag, string needle) =>
        tag.Length == needle.Length ? TagExactScore : TagContainsScore;

    /// <summary>
    /// 数查询词在文本里出现了几次。不重叠计数——<c>aa</c> 在 <c>aaa</c> 里算一次，
    /// 否则同一段文字会因为步进长度不同而给出不同的分数。
    /// </summary>
    private static int CountOccurrences(string text, string needle)
    {
        // 空指针会在下面的循环里原地打转，必须挡在门外。调用方已经过滤过，
        // 但本方法是私有的、将来可能被别处复用，留一道自己的防线比依赖调用方便宜。
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;

        while (true)
        {
            index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                return count;
            }

            count++;
            index += needle.Length;
        }
    }

    private static double KeepHigher(double current, double candidate) => candidate > current ? candidate : current;
}
