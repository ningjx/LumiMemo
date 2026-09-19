namespace LumiMemo.Core.Models;

/// <summary>
/// <c>trash-index.json</c> 中的一条回收站记录（§7.2、§9.2）。
/// </summary>
/// <remarks>
/// 索引是<strong>可重建的派生数据</strong>，不是真数据。用户手动去
/// <c>.lumimemo/trash/</c> 里拖回文件、或手动删掉 trash 里的文件，索引就会与实际不符——
/// 此时按 §7.2 的一致性检查规则修复，而不是报错。
/// </remarks>
public sealed class TrashEntry
{
    /// <summary>在 trash 目录下的实际名字，形如 <c>{删除时刻}-{原文件名}</c>。</summary>
    public required string TrashName { get; init; }

    /// <summary>相对于笔记目录的原路径。恢复时用它（连同 <see cref="TrashName"/>）算出目标位置。</summary>
    public required string OriginalRelativePath { get; set; }

    /// <summary>文件级条目记录便签 id；目录级条目为 <c>null</c>（§7.2）。</summary>
    public Guid? NoteId { get; set; }

    /// <summary>删除时间，用于保留策略（§7.4）。</summary>
    public DateTimeOffset DeletedAt { get; set; }

    public TrashEntryKind Kind { get; init; }

    /// <summary>字节数，仅用于统计展示。</summary>
    public long Size { get; set; }

    /// <summary>
    /// 索引里有这一条，但 <c>trash/</c> 下已经找不到对应的文件或目录（§7.2）。
    /// </summary>
    /// <remarks>
    /// <strong>运行时派生，不写进 <c>trash-index.json</c></strong>：它是「索引与目录对不上」
    /// 这个瞬态事实的描述，下次一致性检查会重新算一遍。落盘只会有两种坏结果——
    /// 要么用户手动把文件拖回来后它仍写着「已不在」，要么条目被当成了真数据。
    /// </remarks>
    public bool IsFileMissing { get; set; }
}
