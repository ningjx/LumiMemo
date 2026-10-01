using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>内存版回收站替身：记录调用，行为由用例脚本化。</summary>
/// <remarks>
/// 真实文件行为（移入/恢复/索引容错）由 Integration.Tests 的
/// FileSystemTrashStoreTests 覆盖；本替身只服务编排逻辑。
/// </remarks>
public sealed class FakeTrashStore : ITrashStore
{
    /// <summary><see cref="ListAsync"/> 要返回的条目。</summary>
    public List<TrashEntry> EntriesToReturn { get; } = [];

    /// <summary>移入回收站的便签，按发生顺序。</summary>
    public List<Note> Moved { get; } = [];

    /// <summary>彻底删除的条目，按发生顺序。</summary>
    public List<TrashEntry> Purged { get; } = [];

    /// <summary>恢复请求：条目 id → 恢复后返回的路径。</summary>
    public Dictionary<Guid, string> RestorePaths { get; } = [];

    /// <summary>设了就让 <see cref="RestoreAsync"/> 抛它。</summary>
    public Exception? RestoreException { get; set; }

    public Task MoveToTrashAsync(Note note, CancellationToken ct = default)
    {
        Moved.Add(note);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrashEntry>>(EntriesToReturn);

    public Task<string> RestoreAsync(TrashEntry entry, CancellationToken ct = default)
    {
        if (RestoreException is { } exception)
        {
            return Task.FromException<string>(exception);
        }

        return Task.FromResult(RestorePaths.TryGetValue(entry.NoteId, out string? path)
            ? path
            : $@"D:\notes\{entry.NoteId:N}.lumi");
    }

    public Task PurgeAsync(TrashEntry entry, CancellationToken ct = default)
    {
        Purged.Add(entry);

        return Task.CompletedTask;
    }

    public Task PurgeExpiredAsync(TimeSpan retention, CancellationToken ct = default) =>
        Task.CompletedTask;
}
