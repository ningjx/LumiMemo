using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Search;

/// <summary>本地关键词搜索：<see cref="NoteSearch"/> 的薄封装，负责筛选与排序策略。</summary>
/// <remarks>
/// 全部是内存里的纯计算，<c>SearchAsync</c> 同步完成——异步签名是为
/// <see cref="INoteSearchProvider"/> 的 AI 实现预留的，这里先用
/// <see cref="Task.FromResult{TResult}"/> 满足契约。
/// </remarks>
public sealed class KeywordSearchProvider : INoteSearchProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        NoteSearchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<Note> notes = request.Color is { } color
            ? [.. request.Notes.Where(note => note.Color == color)]
            : request.Notes;

        string query = request.Query.Trim();

        if (query.Length == 0)
        {
            // 没有查询词：筛选照常生效，顺序按修改时间倒序
            // （「相关度」在没有查询时就是这个语义）。分数与位置是占位值。
            return Task.FromResult<IReadOnlyList<SearchHit>>(
            [
                .. NoteSearch.OrderForList(notes)
                    .Select(static note => new SearchHit(note, -1, [], -1, 0)),
            ]);
        }

        IReadOnlyList<SearchHit> hits = NoteSearch.Search(
            notes, query, request.TopMostIds, request.Now);

        if (request.Sort == NoteSortOrder.ModifiedTime)
        {
            // 相关度序 → 修改时间序：重排但保留命中信息（位置/标签/分数）。
            hits =
            [
                .. hits.OrderByDescending(static hit => hit.Note.UpdatedAt)
                    .ThenBy(static hit => hit.Note.Id),
            ];
        }

        return Task.FromResult(hits);
    }
}
