using System.Text.Json.Serialization;
using LumiMemo.Core.Models;

namespace LumiMemo.Infrastructure.Trash;

/// <summary>
/// <c>trash-index.json</c> 的磁盘形态（§7.2）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>settings.json</c> / <c>layout.json</c> 一样，每个字段都写死
/// <see cref="JsonPropertyNameAttribute"/>：键名是磁盘契约，改 C# 属性名不该静默改掉它（§8.4）。
/// </para>
/// <para>
/// 序列化选项复用 <c>JsonFileFormat.Options</c>，于是时间写成 §7.2 示例里的
/// <c>2026-09-19T12:00:00+08:00</c>、<c>kind</c> 写成小写的 <c>file</c> / <c>directory</c>。
/// </para>
/// </remarks>
internal sealed class TrashIndexFileModel
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = FileSystemTrashStore.CurrentVersion;

    [JsonPropertyName("entries")]
    public List<TrashEntryModel> Entries { get; set; } = [];
}

/// <summary><see cref="TrashEntry"/> 的磁盘形态。</summary>
/// <remarks>
/// <see cref="TrashEntry.IsFileMissing"/> <strong>不在其中</strong>：那是每次一致性检查重算的
/// 运行时事实，不是索引内容。理由见 <c>TrashEntry</c> 上的说明。
/// </remarks>
internal sealed class TrashEntryModel
{
    [JsonPropertyName("trashName")]
    public string TrashName { get; set; } = string.Empty;

    [JsonPropertyName("originalRelativePath")]
    public string OriginalRelativePath { get; set; } = string.Empty;

    [JsonPropertyName("noteId")]
    public Guid? NoteId { get; set; }

    [JsonPropertyName("deletedAt")]
    public DateTimeOffset DeletedAt { get; set; }

    [JsonPropertyName("kind")]
    public TrashEntryKind Kind { get; set; } = TrashEntryKind.File;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    public static TrashEntryModel From(TrashEntry entry) => new()
    {
        TrashName = entry.TrashName,
        OriginalRelativePath = entry.OriginalRelativePath,
        NoteId = entry.NoteId,
        DeletedAt = entry.DeletedAt,
        Kind = entry.Kind,
        Size = entry.Size,
    };

    public TrashEntry ToEntry() => new()
    {
        TrashName = TrashName,
        OriginalRelativePath = OriginalRelativePath,
        NoteId = NoteId,
        DeletedAt = DeletedAt,
        Kind = Kind,
        Size = Size,
    };
}
