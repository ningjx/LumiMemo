using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>回收站：删除的便笺先进这里，可恢复，也可彻底删除。</summary>
/// <remarks>
/// <para>
/// <strong>真源是回收站目录里的文件本身</strong>，索引只提供标题与删除时间这类元数据。
/// 索引损坏或丢失时文件仍在——<see cref="ListAsync"/> 以目录扫描为准，
/// 缺失的元数据按文件时间降级，绝不因为索引坏了就「丢」了用户的便笺。
/// </para>
/// <para>
/// 回收站目录与索引都在笔记目录下的 <c>.lumimemo</c> 元数据目录里（§5.1 的约定），
/// 跟着笔记目录走。
/// </para>
/// </remarks>
public interface ITrashStore
{
    /// <summary>把便笺文件移入回收站并记入索引。</summary>
    Task MoveToTrashAsync(Note note, CancellationToken ct = default);

    /// <summary>列出回收站条目，按删除时间倒序（最近删的在前）。</summary>
    Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>把条目恢复回笔记目录。</summary>
    /// <returns>恢复后的文件完整路径。</returns>
    /// <remarks>
    /// 目标文件已存在时抛 <see cref="InvalidOperationException"/>——同名意味着同 id，
    /// 说明这张便笺已经在笔记目录里了，覆盖它不是本方法该做的决定。
    /// </remarks>
    Task<string> RestoreAsync(TrashEntry entry, CancellationToken ct = default);

    /// <summary>彻底删除一个条目（不可恢复）。</summary>
    Task PurgeAsync(TrashEntry entry, CancellationToken ct = default);

    /// <summary>清理超过 <paramref name="retention"/> 保留期的条目。</summary>
    Task PurgeExpiredAsync(TimeSpan retention, CancellationToken ct = default);
}
