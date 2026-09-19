using LumiMemo.Core.Models;

namespace LumiMemo.Core.Search;

/// <summary>
/// 一条搜索命中：便签本身、它在哪几个位置命中了，以及算出来的分数（§12.1、§12.2）。
/// </summary>
/// <remarks>
/// <para>
/// 位置用 <c>-1</c> 表示"这一处没命中"，与 <see cref="string.IndexOf(string, StringComparison)"/>
/// 的返回值一致，调用方不必再做一层映射。
/// </para>
/// <para>
/// <see cref="Score"/> 只用于排序，不对外展示——它的绝对值没有意义，
/// 有意义的是它与别的命中之间的相对大小。
/// </para>
/// </remarks>
/// <param name="Note">命中的便签。</param>
/// <param name="TitlePosition">查询词在标题里第一次出现的位置；没命中为 <c>-1</c>。</param>
/// <param name="MatchedTags">命中的标签（可能是多个，只要有一个命中就算）。</param>
/// <param name="BodyPosition">查询词在纯文本正文里第一次出现的位置；没命中为 <c>-1</c>。</param>
/// <param name="Score">§12.2 算出的分数，越高越靠前。</param>
public sealed record SearchHit(
    Note Note,
    int TitlePosition,
    IReadOnlyList<string> MatchedTags,
    int BodyPosition,
    double Score)
{
    /// <summary>正文是否命中。只有正文命中才生成摘要（§12.3）。</summary>
    public bool HasBodyMatch => BodyPosition >= 0;
}
