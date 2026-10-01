namespace LumiMemo.Core.Models;

/// <summary>回收站里的一条便笺。</summary>
/// <remarks>
/// 真源是回收站目录里的 <c>.lumi</c> 文件本身；本类型是按文件与索引对账出来的
/// 展示数据。<see cref="Title"/> 是删除时的快照——回收站列表直接显示，
/// 不必解析已经被移走的文件。
/// </remarks>
public sealed class TrashEntry
{
    /// <summary>便笺身份；恢复时按它推回文件名。</summary>
    public required Guid NoteId { get; init; }

    /// <summary>删除时的标题快照。</summary>
    public required string Title { get; init; }

    /// <summary>删除时间，回收站按它倒序排列并计算保留期。</summary>
    public required DateTimeOffset DeletedAt { get; init; }
}
