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
    /// 把条目移回笔记目录。目标位置已被占用时<strong>重命名而不是覆盖</strong>（§7.3）。
    /// </summary>
    /// <param name="entry">要恢复的条目。</param>
    /// <param name="targetRelativePath">
    /// 恢复到哪个相对路径。传 <see langword="null"/> 表示回到
    /// <see cref="TrashEntry.OriginalRelativePath"/>。
    /// </param>
    /// <param name="ct">取消标记。</param>
    /// <returns>实际恢复到的相对路径，可能与请求的不同（撞名时被加了序号）。</returns>
    /// <remarks>
    /// <strong>不做「恢复到根目录还是原地」的判断</strong>：§7.3 的三选一是给用户看的对话框，
    /// 判断落点属于上层。这里只认「往哪放」这一个事实，两个选项于是共用同一条路径——
    /// 差别只是 <paramref name="targetRelativePath"/> 传什么。
    /// 目标路径<strong>必须落在笔记目录内</strong>（§19.4），越界时抛异常而不是照做。
    /// </remarks>
    Task<string> RestoreAsync(
        TrashEntry entry,
        string? targetRelativePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// 条目原本的位置现在被占着吗（§7.3）。
    /// </summary>
    /// <remarks>
    /// 存在只为了让上层能在<strong>动手之前</strong>问用户「重命名 / 恢复到根目录 / 取消」。
    /// 若没有这个方法，上层只能先恢复再看返回值变没变，那时文件已经挪回去了，
    /// 「取消」这个选项就没了。
    /// </remarks>
    bool IsOriginalPathOccupied(TrashEntry entry);

    /// <summary>清空回收站，删除全部内容并清空索引。</summary>
    Task EmptyAsync(CancellationToken ct = default);

    /// <summary>清理超过保留期的条目（§7.4）。</summary>
    /// <returns>被清理的条目数。</returns>
    Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default);
}
