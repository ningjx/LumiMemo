namespace LumiMemo.Core.Models;

/// <summary>便签列表的排序方式。</summary>
/// <remarks>
/// 从「相关度」起步是因为搜索结果天然该按匹配程度排；将来要加「创建时间」「标题」
/// 时在这里扩成员，并在 <c>KeywordSearchProvider</c> 的排序分支里落地。
/// </remarks>
public enum NoteSortOrder
{
    /// <summary>相关度：搜索评分高的在前；没有查询词时等同修改时间倒序。</summary>
    Relevance,

    /// <summary>修改时间倒序（最近改过的在前）。</summary>
    ModifiedTime,
}
