using LumiMemo.Core.Models;

namespace LumiMemo.Core.Search;

/// <summary>
/// 搜索的匹配与排序（§12.1、§12.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>纯函数，不进 DI、没有状态。</strong>列表由调用方从 <c>NoteStore</c> 取快照传进来，
/// 纯文本由调用方从 <c>SearchIndex</c> 取——本类不认识那两个类型，于是不必为它俩造替身就能测。
/// </para>
/// <para>
/// 之所以不把搜索挂在 <c>SearchIndex</c> 上：本方法需要<strong>两份</strong>它没有的东西——
/// 便签列表（在 <c>NoteStore</c>）与置顶状态（在 <c>LayoutService</c>），
/// 而 <c>SearchIndex</c> 只是一个「从便签派生出来的、不落盘的」辅助索引（§9.4），
/// 让反向依赖两个上游容器是错的。组合由管理器 ViewModel 做。
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
    /// 按查询词筛出命中的便签并排好序（§12.1、§12.2）。
    /// </summary>
    /// <param name="notes">候选便签，通常是 <c>NoteStore.Snapshot()</c>。</param>
    /// <param name="query">用户输入的查询词。空白查询返回空列表，见下。</param>
    /// <param name="plainTextOf">取某张便签的纯文本正文，通常是 <c>SearchIndex.GetPlainText</c>。</param>
    /// <param name="topMostIds">当前处于置顶的便签 id，用于 <see cref="TopMostBonus"/>。可为 <c>null</c>。</param>
    /// <param name="now">用来算"最近改过"的当前时刻。显式传入是为了让用例不受真实时钟影响。</param>
    /// <remarks>
    /// <para>
    /// <strong>空查询返回空列表，不是"匹配到全部"。</strong>管理器的列表在查询词为空时压根不走这里，
    /// 而是直接取快照按 <see cref="OrderForList"/> 展示（§12.1、§15.8）。这条区分很重要：
    /// 若让空查询返回全部，那些"分数很低但确实匹配"的规则会把整个列表重排一遍，
    /// 用户会在清空输入框的瞬间看到列表乱跳。
    /// </para>
    /// <para>
    /// <strong>匹配用 <see cref="StringComparison.OrdinalIgnoreCase"/> 的子串查找。</strong>
    /// 中文没有大小写问题，英文按字节序忽略大小写即可；不做分词，因为子串匹配对中文
    /// 比分词或 bigram 更精确（§12.1）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SearchHit> Search(
        IReadOnlyList<Note> notes,
        string? query,
        Func<Guid, string> plainTextOf,
        IReadOnlySet<Guid>? topMostIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(plainTextOf);

        var needle = query?.Trim();

        if (string.IsNullOrEmpty(needle))
        {
            return [];
        }

        var hits = new List<SearchHit>();

        foreach (var note in notes)
        {
            var plain = plainTextOf(note.Id) ?? string.Empty;

            var titlePosition = note.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            var matchedTags = MatchTags(note.Tags, needle);
            var bodyPosition = plain.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

            if (titlePosition < 0 && matchedTags.Count == 0 && bodyPosition < 0)
            {
                continue;
            }

            var score = Score(note, plain, needle, titlePosition, matchedTags, bodyPosition, topMostIds, now);

            hits.Add(new SearchHit(note, titlePosition, matchedTags, bodyPosition, score));
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

    /// <summary>算出 §12.2 的分数。</summary>
    /// <remarks>
    /// <para>
    /// <strong>六条基础分取最高、不相加。</strong>表一是一张优先级阶梯（标题完全等于 1000
    /// 明显是要压过一切的信号），若相加，一条"标题包含 + 标签精确 + 正文命中"的便签会拿到
    /// 600+500+200=1300 分，反过来压过标题完全等于查询的那条——阶梯就失效了。
    /// </para>
    /// <para>
    /// <strong>开头的倍数只加在正文那一条上。</strong>若把 ×1.5 乘在总分上，
    /// 一条"标题命中、正文恰好在开头也出现"的便签会平白多拿五成——可它之所以排前面
    /// 靠的是标题，与正文开头没关系。所以乘在候选分内部，再参与取最高。
    /// </para>
    /// </remarks>
    private static double Score(
        Note note,
        string plain,
        string needle,
        int titlePosition,
        IReadOnlyList<string> matchedTags,
        int bodyPosition,
        IReadOnlySet<Guid>? topMostIds,
        DateTimeOffset now)
    {
        var best = 0.0;

        if (titlePosition >= 0)
        {
            best = KeepHigher(best, TitleScore(note.Title, needle, titlePosition));
        }

        foreach (var tag in matchedTags)
        {
            best = KeepHigher(best, TagScore(tag, needle));
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

        var occurrences = CountOccurrences(plain, needle);
        if (occurrences > MaxCountedOccurrences)
        {
            occurrences = MaxCountedOccurrences;
        }

        best += occurrences * PerOccurrenceBonus;

        if (topMostIds?.Contains(note.Id) == true)
        {
            best += TopMostBonus;
        }

        // 未来时间戳（时钟回拨、外部编辑器写了个超前的时间）也会落进"七天内"这一档，
        // 不给它单开一个分支：加 30 分与加 0 分的差别不值得多一条规则。
        var age = now - note.UpdatedAt;

        if (age <= TimeSpan.FromDays(7))
        {
            best += RecentWeekBonus;
        }
        else if (age <= TimeSpan.FromDays(30))
        {
            best += RecentMonthBonus;
        }

        if (string.IsNullOrWhiteSpace(plain))
        {
            best += EmptyBodyPenalty;
        }

        return best;
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

    private static List<string> MatchTags(List<string> tags, string needle)
    {
        var matched = new List<string>();

        foreach (var tag in tags)
        {
            if (tag.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(tag);
            }
        }

        return matched;
    }

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
