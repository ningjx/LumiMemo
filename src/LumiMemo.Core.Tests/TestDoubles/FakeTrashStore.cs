using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="ITrashStore"/> 替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 真实的搬运规则（命名、索引对账、撞名恢复、保留期）由
/// <c>LumiMemo.Integration.Tests</c> 里的 <c>FileSystemTrashStoreTests</c> 用真实目录覆盖。
/// 本替身只服务 <c>NoteService</c> 与 <c>TrashService</c> 的编排：
/// <strong>它们有没有在该转发的时候转发、该摘内存的时候摘内存</strong>。
/// </remarks>
public sealed class FakeTrashStore : ITrashStore
{
    /// <summary>回收站里现有的条目，由用例预先放好。</summary>
    public List<TrashEntry> Entries { get; } = [];

    /// <summary>所有被送进 <see cref="MoveFileToTrashAsync"/> 的便签 id，按发生顺序。</summary>
    public List<Guid?> MovedNoteIds { get; } = [];

    /// <summary>被送进来的相对路径，与 <see cref="MovedNoteIds"/> 一一对应。</summary>
    public List<string> MovedPaths { get; } = [];

    /// <summary>原路径被占用的条目，按 <c>TrashName</c> 记。</summary>
    public HashSet<string> Occupied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="PurgeExpiredAsync"/> 要返回的条数。</summary>
    public int PurgeResult { get; set; }

    /// <summary><see cref="RestoreAsync"/> 实际落到的相对路径；为 <see langword="null"/> 时按请求原样返回。</summary>
    public string? RestoreResultOverride { get; set; }

    public int ListCallCount { get; private set; }

    public int EmptyCallCount { get; private set; }

    public int? LastRetentionDays { get; private set; }

    /// <summary>所有被请求恢复的 (条目, 目标路径)。</summary>
    public List<(TrashEntry Entry, string? Target)> Restores { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default)
    {
        ListCallCount++;

        return Task.FromResult<IReadOnlyList<TrashEntry>>([.. Entries]);
    }

    /// <inheritdoc />
    public Task<TrashEntry> MoveFileToTrashAsync(
        string relativePath,
        Guid? noteId,
        CancellationToken ct = default)
    {
        MovedPaths.Add(relativePath);
        MovedNoteIds.Add(noteId);

        var entry = new TrashEntry
        {
            TrashName = $"20260101-000000-{Path.GetFileName(relativePath)}",
            OriginalRelativePath = relativePath,
            NoteId = noteId,
            DeletedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Kind = TrashEntryKind.File,
        };

        Entries.Add(entry);

        return Task.FromResult(entry);
    }

    /// <inheritdoc />
    public Task<TrashEntry> MoveDirectoryToTrashAsync(string relativeDirectory, CancellationToken ct = default)
    {
        MovedPaths.Add(relativeDirectory);
        MovedNoteIds.Add(null);

        var entry = new TrashEntry
        {
            TrashName = $"20260101-000000-{Path.GetFileName(relativeDirectory)}",
            OriginalRelativePath = relativeDirectory,
            DeletedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Kind = TrashEntryKind.Directory,
        };

        Entries.Add(entry);

        return Task.FromResult(entry);
    }

    /// <inheritdoc />
    public Task<string> RestoreAsync(
        TrashEntry entry,
        string? targetRelativePath = null,
        CancellationToken ct = default)
    {
        Restores.Add((entry, targetRelativePath));

        return Task.FromResult(
            RestoreResultOverride ?? targetRelativePath ?? entry.OriginalRelativePath);
    }

    /// <inheritdoc />
    public bool IsOriginalPathOccupied(TrashEntry entry) => Occupied.Contains(entry.TrashName);

    /// <inheritdoc />
    public Task EmptyAsync(CancellationToken ct = default)
    {
        EmptyCallCount++;
        Entries.Clear();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default)
    {
        LastRetentionDays = retentionDays;

        return Task.FromResult(PurgeResult);
    }
}
