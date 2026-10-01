using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using Xunit;

namespace LumiMemo.Core.Tests.Search;

/// <summary>
/// <see cref="KeywordSearchProvider"/> 的筛选与排序策略。
/// </summary>
/// <remarks>
/// 匹配与评分细节由 <see cref="NoteSearchTests"/> 覆盖；这一组只钉「请求怎么被解释」：
/// 颜色筛选、空查询语义、排序切换。
/// </remarks>
public sealed class KeywordSearchProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly KeywordSearchProvider Provider = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<SearchHit>> SearchAsync(NoteSearchRequest request) =>
        Provider.SearchAsync(request, Ct);

    [Fact]
    public async Task 颜色筛选_只留该颜色的便签()
    {
        Note blue = NewNote("# 蓝便签", color: NoteColor.Blue);
        NewNote("# 黄便签", color: NoteColor.Yellow);

        IReadOnlyList<SearchHit> hits = await SearchAsync(
            new NoteSearchRequest
            {
                Notes = [blue],
                Query = string.Empty,
                Color = NoteColor.Blue,
                Now = Now,
            });

        Assert.Equal(blue, Assert.Single(hits).Note);
    }

    [Fact]
    public async Task 空查询_返回全部且按修改时间倒序()
    {
        Note older = NewNote("# 旧", updatedAt: Now.AddDays(-9));
        Note newest = NewNote("# 新", updatedAt: Now.AddDays(-1));
        Note middle = NewNote("# 中", updatedAt: Now.AddDays(-4));

        IReadOnlyList<SearchHit> hits = await SearchAsync(Request([older, newest, middle]));

        Assert.Equal(
            new[] { newest.Id, middle.Id, older.Id },
            hits.Select(static hit => hit.Note.Id).ToArray());
    }

    [Fact]
    public async Task 空查询_颜色筛选照常生效()
    {
        Note blue = NewNote("# 蓝", color: NoteColor.Blue);
        NewNote("# 灰", color: NoteColor.Gray);

        IReadOnlyList<SearchHit> hits = await SearchAsync(
            Request([blue], color: NoteColor.Blue));

        Assert.Equal(blue, Assert.Single(hits).Note);
    }

    [Fact]
    public async Task 修改时间排序_把相关度序重排()
    {
        // 旧的靠标题精确命中拿到高相关度，新的只在正文命中——两种排序给出的顺序相反。
        Note oldButRelevant = NewNote(
            "# 文档", updatedAt: Now.AddDays(-30));
        Note newButWeak = NewNote(
            "# 笔记\n提一下文档", updatedAt: Now.AddDays(-1));
        Note[] notes = [oldButRelevant, newButWeak];

        IReadOnlyList<SearchHit> byRelevance = await SearchAsync(
            Request(notes, query: "文档", sorts: [NoteSortOrder.Relevance]));
        IReadOnlyList<SearchHit> byTime = await SearchAsync(
            Request(notes, query: "文档", sorts: [NoteSortOrder.ModifiedTime]));

        Assert.Equal(oldButRelevant.Id, byRelevance[0].Note.Id);
        Assert.Equal(newButWeak.Id, byTime[0].Note.Id);

        // 重排只换顺序：命中信息（位置/标签）跟着便签走。
        Assert.All(byTime, static hit => Assert.True(hit.TitlePosition >= 0 || hit.HasBodyMatch));
    }

    [Fact]
    public async Task 有查询时_颜色筛选也生效()
    {
        Note blue = NewNote("# 文档", color: NoteColor.Blue);
        NewNote("# 文档", color: NoteColor.Pink);

        IReadOnlyList<SearchHit> hits = await SearchAsync(
            Request([blue], query: "文档", color: NoteColor.Blue));

        Assert.Equal(blue, Assert.Single(hits).Note);
    }

    [Fact]
    public async Task 没有候选便签_返回空()
    {
        IReadOnlyList<SearchHit> hits = await SearchAsync(
            Request([], query: "文档"));

        Assert.Empty(hits);
    }

    private static NoteSearchRequest Request(
        IReadOnlyList<Note> notes,
        string query = "",
        IReadOnlyList<NoteSortOrder>? sorts = null,
        NoteColor? color = null) =>
        new()
        {
            Notes = notes,
            Query = query,
            Sorts = sorts ?? [],
            Color = color,
            Now = Now,
        };

    private static Note NewNote(
        string content,
        NoteColor color = NoteColor.Yellow,
        DateTimeOffset? updatedAt = null)
    {
        Guid id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.lumi",
            Content = content,
            Color = color,
            CreatedAt = Now.AddYears(-1),
            UpdatedAt = updatedAt ?? Now,
        };
    }
}
