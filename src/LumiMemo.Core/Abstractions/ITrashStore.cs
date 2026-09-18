using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 回收站的磁盘操作（§7）。实现在 Infrastructure 层，操作笔记目录下的
/// <c>.lumimemo/trash/</c> 与 <c>trash-index.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 回收站放在笔记目录<strong>内</strong>是硬性要求：只有同卷，<c>File.Move</c> 才是原子操作。
/// 跨卷会退化成「复制 + 删除」，中途失败就丢数据（§7.1）。
/// </para>
/// <para>
/// 索引是<strong>可重建的派生数据</strong>：实现必须在索引与实际目录内容不一致时按
/// §7.2 的表格修复（从文件名解析时间戳与原名补条目、移除已不存在的条目），
/// 而不是报错或覆盖用户手动放进来的文件。
/// </para>
/// </remarks>
public interface ITrashStore
{
    /// <summary>列出全部条目，并在返回前完成一次 §7.2 的一致性修复。</summary>
    Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>把一个便签文件移入回收站。文件名会加上删除时刻前缀以避免重名冲突（§7.1）。</summary>
    /// <param name="relativePath">相对于笔记目录的原路径。</param>
    /// <param name="noteId">便签 id；目录级条目传 <c>null</c>。</param>
    Task<TrashEntry> MoveFileToTrashAsync(string relativePath, Guid? noteId, CancellationToken ct = default);

    /// <summary>
    /// 把<strong>整个目录</strong>移入回收站（§5.7 的「整个文件夹移到回收站」选项）。
    /// 这是整体移动，不是逐个移文件——恢复时也用 <c>Directory.Move</c>。
    /// </summary>
    Task<TrashEntry> MoveDirectoryToTrashAsync(string relativeDirectory, CancellationToken ct = default);

    /// <summary>
    /// 恢复到原位置。若原位置已有同名文件，<strong>重命名而不是覆盖</strong>（§7.3）。
    /// </summary>
    /// <returns>实际恢复到的相对路径，可能与请求的不同。</returns>
    Task<string> RestoreAsync(TrashEntry entry, CancellationToken ct = default);

    /// <summary>清空回收站，删除全部内容并清空索引。</summary>
    Task EmptyAsync(CancellationToken ct = default);

    /// <summary>清理超过保留期的条目（§7.4）。</summary>
    /// <returns>被清理的条目数。</returns>
    Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default);
}
