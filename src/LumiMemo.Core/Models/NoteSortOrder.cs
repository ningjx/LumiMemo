namespace LumiMemo.Core.Models;

/// <summary>便签列表的一种排序键；多键按选择顺序组成排序链（见 <c>NoteSearch.SortHits</c>）。</summary>
/// <remarks>
/// 链语义：第一个键是主键、依次递补；每个键都是倒序（新/高分在前），末键 id 升序兜底。
/// </remarks>
public enum NoteSortOrder
{
    /// <summary>相关度：搜索评分高的在前。<strong>仅在有查询词时有意义</strong>，无查询词时该键被跳过。</summary>
    Relevance,

    /// <summary>修改时间倒序（最近改过的在前）。</summary>
    ModifiedTime,

    /// <summary>创建时间倒序（新建的在前）。</summary>
    CreatedTime,
}
