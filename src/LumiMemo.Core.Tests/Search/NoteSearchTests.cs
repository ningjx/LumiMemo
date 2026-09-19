using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using LumiMemo.Core.Stores;
using Xunit;

namespace LumiMemo.Core.Tests.Search;

/// <summary>
/// <see cref="NoteSearch"/> 的单元测试：匹配与评分（§12.1、§12.2）。
/// </summary>
/// <remarks>
/// <para>
/// 断言分两类，各有各的理由。<strong>基础分阶梯</strong>断言的是<em>相对顺序</em>——
/// 那张表的含义就是"谁排在谁前面"，逐个钉死绝对分数只会让用例在调整任一常数时全红，
/// 却说不清到底哪一档坏了。<strong>修正项</strong>断言的是<em>两个几乎相同的便签之间的分差</em>——
/// 修正项的意义就是差值本身，绝对值没有含义。
/// </para>
/// <para>
/// 纯文本走真实的 <see cref="SearchIndex"/>，不自己塞一份假的：标题那一行本来就会
/// 进纯文本（<c>GetPlainText</c> 拿的是整篇正文），自己造一份会让用例里的
/// "只命中正文"变成现实中不存在的形态，也会让"出现次数加分"的期望值凭空差一次。
/// </para>
/// </remarks>
public sealed class NoteSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // ================= 空查询 =================

    [Fact]
    public void 空查询返回空列表而不是全部()
    {
        // §12.1 的关键语义。若这里返回全部，管理器清空搜索框的瞬间会走一遍评分排序，
        // 用户会看到列表跳一下。
        var notes = new[] { NewNote("# 文档"), NewNote("# 笔记") };

        Assert.Empty(Search("", notes));
        Assert.Empty(Search("   ", notes));
        Assert.Empty(Search(null, notes));
    }

    [Fact]
    public void 查询词两端空白会被裁掉()
    {
        Assert.Single(Search("  文档  ", [NewNote("# 文档")]));
    }

    [Fact]
    public void 没有任何命中时返回空列表()
    {
        Assert.Empty(Search("找不到的词", [NewNote("# 文档")]));
    }

    // ================= 基础分阶梯（§12.2 表一）=================

    [Fact]
    public void 基础分阶梯_标题高于标签高于正文()
    {
        var titleExact = NewNote("# 文档");
        var titlePrefix = NewNote("# 文档草稿");
        var titleContains = NewNote("# 我的文档");

        var tagExact = NewNote("# 笔记");
        tagExact.Tags.Add("文档");

        var tagContains = NewNote("# 笔记");
        tagContains.Tags.Add("文档库");

        var bodyOnly = NewNote("# 笔记\n提一下文档");

        // 打乱传进去，断言的是排序结果而不是输入顺序。
        var hits = Search("文档", [bodyOnly, tagContains, tagExact, titleContains, titlePrefix, titleExact]);

        Assert.Equal(
            new[] { titleExact, titlePrefix, titleContains, tagExact, tagContains, bodyOnly },
            hits.Select(static h => h.Note).ToArray());
    }

    [Fact]
    public void 标题完全等于与标签完全等于不叠加_只取高的那条()
    {
        // 六条基础分是一张优先级阶梯，取最高而不是相加。若相加，
        // "标题包含 + 标签精确 + 正文命中"的便签会拿到 1300 分，反过来压过标题完全等于的那条。
        //
        // 两条便签的 id 必须写死：同分时的兜底键是"修改时间倒序、再按 id 升序"，
        // 而这两条的修改时间一模一样。用随机 Guid 的话，谁在前每次跑都不一样——
        // 断言顺序的那一行会有大约一半的概率红。
        var both = NewNote("# 文档", id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        both.Tags.Add("文档");

        var titleOnly = NewNote("# 文档", id: Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        var hits = Search("文档", [both, titleOnly]);

        Assert.Equal(both, hits[0].Note);
        Assert.Equal(hits[0].Score, hits[1].Score, precision: 10);
    }

    // ================= 修正项（§12.2 表二）=================

    [Fact]
    public void 正文命中落在开头时加成_落在后面则没有()
    {
        // 两边正文长度对称，只有命中位置不同；标题都不含查询词，
        // 于是两条唯一的差别就是"开头加权"。
        var padding = new string('的', 200);

        var atStart = NewNote("# 笔记\n文档" + padding);
        var farInside = NewNote("# 笔记\n" + padding + "文档");

        var hits = Search("文档", [atStart, farInside]);

        Assert.Equal(atStart, hits[0].Note);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void 正文出现次数越多分越高()
    {
        var once = NewNote("# 笔记\n文档 一二三四五六七八九十");
        var threeTimes = NewNote("# 笔记\n文档 文档 文档");

        var hits = Search("文档", [once, threeTimes]);

        Assert.Equal(threeTimes, hits[0].Note);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void 出现次数加分有上限()
    {
        // 上限的作用是别让一份刷屏的长文靠"出现得多"压过一切。
        var tenTimes = NewNote("# 笔记\n" + string.Join(' ', Enumerable.Repeat("文档", 10)));
        var twentyTimes = NewNote("# 笔记\n" + string.Join(' ', Enumerable.Repeat("文档", 20)));

        var hits = Search("文档", [tenTimes, twentyTimes]);

        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Score, hits[1].Score, precision: 10);
    }

    [Fact]
    public void 置顶的便签加五十分()
    {
        var pinned = NewNote("# 笔记\n文档在这里放着");
        var plain = NewNote("# 笔记\n文档在这里放着");

        var hits = Search("文档", [pinned, plain], new HashSet<Guid> { pinned.Id });

        Assert.Equal(pinned, hits[0].Note);
        Assert.Equal(NoteSearch.TopMostBonus, hits[0].Score - hits[1].Score, precision: 10);
    }

    [Fact]
    public void 最近改过的便签加更多分()
    {
        var today = NewNote("# 笔记\n文档在这里放着", updatedAt: Now.AddDays(-1));
        var lastMonth = NewNote("# 笔记\n文档在这里放着", updatedAt: Now.AddDays(-20));
        var longAgo = NewNote("# 笔记\n文档在这里放着", updatedAt: Now.AddDays(-200));

        var hits = Search("文档", [today, lastMonth, longAgo]);

        var weekGap = hits[0].Score - hits[1].Score;
        var monthGap = hits[1].Score - hits[2].Score;

        Assert.Equal(NoteSearch.RecentWeekBonus - NoteSearch.RecentMonthBonus, weekGap, precision: 10);
        Assert.Equal(NoteSearch.RecentMonthBonus, monthGap, precision: 10);
    }

    [Fact]
    public void 整篇正文为空的便签要扣分()
    {
        // 只有"整篇正文都是空的"才落进这一档。一张只有标题的便签不属于此列——
        // 标题那一行本身也算正文，于是它反而会因为"出现一次"拿到次数加分。
        // 两条便签都只命中标签，把标题与正文那两档的影响摘干净，分差才正好是这一项。
        var blank = NewNote(string.Empty);
        blank.Tags.Add("文档");

        var filled = NewNote("# 笔记\n有点内容");
        filled.Tags.Add("文档");

        var hits = Search("文档", [blank, filled]);

        Assert.Equal(filled, hits[0].Note);
        Assert.Equal(NoteSearch.EmptyBodyPenalty, hits[1].Score - hits[0].Score, precision: 10);
    }

    // ================= 匹配行为（§12.1）=================

    [Fact]
    public void 匹配忽略大小写()
    {
        var note = NewNote("# Docker 笔记");

        Assert.Single(Search("docker", [note]));
        Assert.Single(Search("DOCKER", [note]));
    }

    [Fact]
    public void 同一张便签的多处命中都记下来()
    {
        var note = NewNote("# 我的文档\n正文里也有文档");

        var hit = Assert.Single(Search("文档", [note]));

        Assert.True(hit.TitlePosition >= 0);
        Assert.True(hit.HasBodyMatch);
        Assert.Empty(hit.MatchedTags);
    }

    [Fact]
    public void 多个标签命中时全部记下来()
    {
        var note = NewNote("# 笔记");
        note.Tags.Add("文档");
        note.Tags.Add("文档库");
        note.Tags.Add("别的");

        var hit = Assert.Single(Search("文档", [note]));

        Assert.Equal(new[] { "文档", "文档库" }, hit.MatchedTags.ToArray());
    }

    [Fact]
    public void 只命中标签时两处位置都是负一()
    {
        var note = NewNote("# 笔记");
        note.Tags.Add("文档");

        var hit = Assert.Single(Search("文档", [note]));

        Assert.Equal(-1, hit.TitlePosition);
        Assert.Equal(-1, hit.BodyPosition);
        Assert.False(hit.HasBodyMatch);
    }

    // ================= 确定性（§12.2）=================

    [Fact]
    public void 同分时按修改时间倒序再按id升序()
    {
        // 没有这条兜底键，同一查询两次会得到不同顺序，用户会觉得界面在乱跳。
        var olderSmallId = NewNote(
            "# 笔记\n文档在这里放着",
            updatedAt: Now.AddDays(-3),
            id: Guid.Parse("00000000-0000-0000-0000-000000000001"));

        var olderBigId = NewNote(
            "# 笔记\n文档在这里放着",
            updatedAt: Now.AddDays(-3),
            id: Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        var newer = NewNote(
            "# 笔记\n文档在这里放着",
            updatedAt: Now.AddDays(-1),
            id: Guid.Parse("00000000-0000-0000-0000-0000000000aa"));

        var hits = Search("文档", [olderBigId, newer, olderSmallId]);

        Assert.Equal(
            new[] { newer, olderSmallId, olderBigId },
            hits.Select(static h => h.Note).ToArray());
    }

    [Fact]
    public void 同一批数据两次搜索顺序一致()
    {
        var notes = Enumerable.Range(0, 20)
                              .Select(i => NewNote($"# 笔记 {i}\n文档在这里放着"))
                              .ToArray();

        var first = Search("文档", notes).Select(static h => h.Note.Id).ToArray();
        var second = Search("文档", notes).Select(static h => h.Note.Id).ToArray();

        Assert.Equal(first, second);
    }

    // ================= 列表顺序（§15.8）=================

    [Fact]
    public void 无查询词的列表按修改时间倒序()
    {
        var oldest = NewNote("# 一", updatedAt: Now.AddDays(-9));
        var newest = NewNote("# 二", updatedAt: Now.AddDays(-1));
        var middle = NewNote("# 三", updatedAt: Now.AddDays(-4));

        var ordered = NoteSearch.OrderForList([oldest, newest, middle]);

        Assert.Equal(new[] { newest, middle, oldest }, ordered.ToArray());
    }

    [Fact]
    public void 无查询词的列表在同时间时按id升序()
    {
        var big = NewNote("# 一", id: Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        var small = NewNote("# 二", id: Guid.Parse("00000000-0000-0000-0000-000000000001"));

        var ordered = NoteSearch.OrderForList([big, small]);

        Assert.Equal(new[] { small, big }, ordered.ToArray());
    }

    // ================= 辅助 =================

    private static IReadOnlyList<SearchHit> Search(
        string? query,
        IReadOnlyList<Note> notes,
        IReadOnlySet<Guid>? topMostIds = null)
    {
        var index = new SearchIndex();
        index.Rebuild(notes);

        return NoteSearch.Search(notes, query, index.GetPlainText, topMostIds, Now);
    }

    private static Note NewNote(string content, DateTimeOffset? updatedAt = null, Guid? id = null)
    {
        var noteId = id ?? Guid.NewGuid();

        return new Note
        {
            Id = noteId,
            FilePath = $@"D:\notes\{noteId:N}.md",
            Content = content,
            CreatedAt = Now.AddYears(-1),
            UpdatedAt = updatedAt ?? Now,
        };
    }
}
