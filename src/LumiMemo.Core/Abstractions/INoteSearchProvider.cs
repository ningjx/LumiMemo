using LumiMemo.Core.Models;
using LumiMemo.Core.Search;

namespace LumiMemo.Core.Abstractions;

/// <summary>便签搜索的提供方。</summary>
/// <remarks>
/// <para>
/// 当前唯一的实现是本地关键词搜索（<see cref="KeywordSearchProvider"/>）。
/// <strong>计划中的 AI 搜索（语义检索）将实现同一接口</strong>——接口从第一天就是
/// 异步的，UI 层不必为它改签名；届时由组合根决定注入哪一个实现
/// （或按设置二选一），管理器 ViewModel 无感知。
/// </para>
/// <para>
/// 返回 <see cref="SearchHit"/>（便签 + 命中位置 + 分数）而不是裸的便签列表：
/// 相关度排序、命中高亮与将来的摘要展示都要这些信息，AI 搜索的「相似度」
/// 落在同一个 <c>Score</c> 语义上。
/// </para>
/// </remarks>
public interface INoteSearchProvider
{
    /// <summary>执行一次搜索：筛选 → 匹配 → 排序。</summary>
    /// <remarks>
    /// 没有查询词时<strong>不返回空列表</strong>：筛选与排序照常生效，返回全部
    /// 候选（这是「实时列表」的语义；空查询返回空只适用于纯搜索函数本身）。
    /// </remarks>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        NoteSearchRequest request, CancellationToken ct = default);
}
