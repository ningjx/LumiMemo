using System.IO;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="ITrashStore"/> 替身。
/// </summary>
/// <remarks>
/// 真实的搬运规则（命名、索引对账、撞名恢复、保留期）由集成测试用真实目录覆盖。
/// 本替身只服务回收站界面：<strong>它有没有在该问用户的时候问、该转发的时候转发</strong>。
/// </remarks>
public sealed class FakeTrashStore : ITrashStore
{
    /// <summary>回收站里现有的条目，由用例预先放好。</summary>
    public List<TrashEntry> Entries { get; } = [];

    /// <summary>原路径被占用的条目，按 <c>TrashName</c> 记。为真时会走 §7.3 的三选一。</summary>
    public HashSet<string> Occupied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有被请求恢复的（条目，目标路径）。目标为 <see langword="null"/> 表示回原位。</summary>
    public List<(TrashEntry Entry, string? Target)> Restores { get; } = [];

    /// <summary><see cref="EmptyAsync"/> 被调了几次。<c>0</c> 就说明用户没点到底。</summary>
    public int EmptyCallCount { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrashEntry>>([.. Entries]);

    /// <inheritdoc />
    public Task<TrashEntry> MoveFileToTrashAsync(
        string relativePath,
        Guid? noteId,
        CancellationToken ct = default) =>
        throw new NotSupportedException("回收站界面不发起删除，本替身也就不支持它。");

    /// <inheritdoc />
    public Task<TrashEntry> MoveDirectoryToTrashAsync(
        string relativeDirectory,
        CancellationToken ct = default) =>
        throw new NotSupportedException("回收站界面不发起删除，本替身也就不支持它。");

    /// <inheritdoc />
    public Task<string> RestoreAsync(
        TrashEntry entry,
        string? targetRelativePath = null,
        CancellationToken ct = default)
    {
        Restores.Add((entry, targetRelativePath));

        // 条目被搬走了就不在回收站里了。真实实现同样会把它从索引里摘掉，
        // 少了这一步，「恢复之后列表里那一条该消失」就永远验不到。
        Entries.Remove(entry);

        return Task.FromResult(targetRelativePath ?? entry.OriginalRelativePath);
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
    public Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default) =>
        Task.FromResult(0);

    /// <summary>造一个条目并放进 <see cref="Entries"/>。</summary>
    /// <param name="relativePath">原本在笔记目录里的相对路径。</param>
    /// <param name="noteId">便签 id；<see langword="null"/> 表示目录级条目。</param>
    /// <param name="size">字节数，用于验「共 X MB」那行字。</param>
    /// <param name="deletedAt">删除时刻；不给时取一个固定的值。</param>
    public TrashEntry Add(
        string relativePath,
        Guid? noteId = null,
        long size = 0,
        DateTimeOffset? deletedAt = null)
    {
        var entry = new TrashEntry
        {
            TrashName = $"20260101-000000-{Path.GetFileName(relativePath)}",
            OriginalRelativePath = relativePath,
            NoteId = noteId,
            DeletedAt = deletedAt ?? new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero),
            Kind = noteId is null ? TrashEntryKind.Directory : TrashEntryKind.File,
            Size = size,
        };

        Entries.Add(entry);

        return entry;
    }
}
