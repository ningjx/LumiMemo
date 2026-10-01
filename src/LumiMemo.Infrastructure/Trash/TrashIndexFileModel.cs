namespace LumiMemo.Infrastructure.Trash;

/// <summary>回收站索引 <c>trash-index.json</c> 的文件形态。</summary>
/// <remarks>
/// 属性名即格式契约（与 <c>.lumi</c>、<c>layout.json</c> 的做法一致，不配置命名策略）。
/// 索引只是元数据缓存：读不出来时按「无索引」处理，文件真源不受影响。
/// </remarks>
internal sealed class TrashIndexFileModel
{
    public int Version { get; set; }

    public List<TrashIndexEntry>? Entries { get; set; }
}

internal sealed class TrashIndexEntry
{
    public Guid NoteId { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset DeletedAt { get; set; }
}
