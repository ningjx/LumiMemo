namespace LumiMemo.Core.Models;

/// <summary>一次搜索请求：查询词、筛选、排序，以及当时的数据快照。</summary>
/// <remarks>
/// <para>
/// 便签集合按<strong>每次调用</strong>传入而不是挂在搜索实现上：列表是可变的
/// （新建、删除、标题更新都会改它），搜索方不该持有一份会过期的快照。
/// </para>
/// <para>
/// 请求模型与具体搜索方式无关——本地关键词搜索与将来计划中的 AI 搜索
/// 接收同一种请求（见 <c>INoteSearchProvider</c>）。
/// </para>
/// </remarks>
public sealed class NoteSearchRequest
{
    /// <summary>候选便签（调用方传当前列表的快照引用）。</summary>
    public required IReadOnlyList<Note> Notes { get; init; }

    /// <summary>用户输入的查询词；空白表示「没有搜索」，按排序展示全部。</summary>
    public required string Query { get; init; }

    /// <summary>
    /// 排序链：按列表顺序逐键比较（第一个是主键）；空列表表示「默认排序」——
    /// 有查询词按相关度，无查询词按修改时间倒序。
    /// </summary>
    public IReadOnlyList<NoteSortOrder> Sorts { get; init; } = [];

    /// <summary>颜色筛选；<see langword="null"/> 表示不筛颜色。</summary>
    public NoteColor? Color { get; init; }

    /// <summary>当前置顶的便签 id（相关度加分用）。可为 <see langword="null"/>。</summary>
    public IReadOnlySet<Guid>? TopMostIds { get; init; }

    /// <summary>「现在」——算「最近改过」用；显式传入让用例不受真实时钟影响。</summary>
    public required DateTimeOffset Now { get; init; }
}
