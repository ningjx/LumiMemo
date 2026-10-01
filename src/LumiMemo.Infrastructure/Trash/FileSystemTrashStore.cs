using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Trash;

/// <summary><see cref="ITrashStore"/> 的实现：回收站就在笔记目录的 <c>.lumimemo</c> 元数据目录里。</summary>
/// <remarks>
/// <para>
/// <strong>文件是真源，索引只是元数据</strong>：<see cref="ListAsync"/> 以回收站目录里的
/// <c>*.lumi</c> 为准，按 id 从索引取标题与删除时间；索引读不出来就按文件时间降级显示
/// （标题显示「（未知标题）」），文件一个都不会丢。索引里已经没有对应文件的孤儿条目
/// 会在下一次写索引时被顺手剔除。
/// </para>
/// <para>
/// 回收站目录跟着笔记目录走（<c>{笔记目录}/.lumimemo/trash/</c>），换目录时回收站随行。
/// </para>
/// </remarks>
public sealed class FileSystemTrashStore : ITrashStore
{
    /// <summary>笔记目录下存放程序元数据的目录名（§5.1 的约定）。</summary>
    public const string MetadataDirectoryName = ".lumimemo";

    private const int IndexVersion = 1;

    private readonly string _notesFolder;
    private readonly string _trashFolder;
    private readonly string _indexPath;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly ILogger<FileSystemTrashStore> _logger;

    /// <param name="notesFolder">笔记目录（回收站与索引都放在它下面的 <c>.lumimemo</c> 里）。</param>
    /// <param name="clock">时间来源（删除时间与文件时间戳）。</param>
    /// <param name="writer">索引的原子写盘器。</param>
    /// <param name="logger">日志。</param>
    public FileSystemTrashStore(
        string notesFolder,
        IClock clock,
        AtomicFileWriter writer,
        ILogger<FileSystemTrashStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesFolder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _notesFolder = notesFolder;
        _trashFolder = Path.Combine(notesFolder, MetadataDirectoryName, "trash");
        _indexPath = Path.Combine(notesFolder, MetadataDirectoryName, "trash-index.json");
        _clock = clock;
        _writer = writer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task MoveToTrashAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        Directory.CreateDirectory(_trashFolder);

        // 同 id 覆盖是安全的：回收站里只可能有一次的同名遗留（例如上次删除后索引写失败）。
        File.Move(note.FilePath, TrashPath(note.Id), overwrite: true);

        Dictionary<Guid, IndexEntry> index = await ReadIndexAsync(ct).ConfigureAwait(false);
        index[note.Id] = new IndexEntry(note.Title, _clock.Now);
        await WriteIndexAsync(index, ct).ConfigureAwait(false);

        _logger.LogInformation("便笺已移入回收站：{NoteId}。", note.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_trashFolder))
        {
            return [];
        }

        Dictionary<Guid, IndexEntry> index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var entries = new List<TrashEntry>();

        foreach (string path in Directory.EnumerateFiles(
                     _trashFolder, "*.lumi", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                // 名字不是我们写的形态：不是本程序放进来的东西，不列也不动。
                continue;
            }

            entries.Add(index.TryGetValue(id, out IndexEntry? meta)
                ? new TrashEntry { NoteId = id, Title = meta.Title, DeletedAt = meta.DeletedAt }
                : new TrashEntry
                {
                    NoteId = id,
                    Title = "（未知标题）",
                    DeletedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                });
        }

        return [.. entries.OrderByDescending(static entry => entry.DeletedAt)];
    }

    /// <inheritdoc />
    public async Task<string> RestoreAsync(TrashEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string source = TrashPath(entry.NoteId);
        string target = Path.Combine(_notesFolder, $"{entry.NoteId:N}.lumi");

        if (!File.Exists(source))
        {
            // 文件不在（被外部清掉了）：顺手把索引里的孤儿条目也清掉，再如实报错。
            await RemoveIndexEntryAsync(entry.NoteId, ct).ConfigureAwait(false);
            throw new InvalidOperationException("回收站里已经找不到这张便笺了。");
        }

        if (File.Exists(target))
        {
            throw new InvalidOperationException("笔记目录里已经有一张同 id 的便笺，未覆盖它。");
        }

        // 先移文件再改索引：顺序反过来的话，移文件失败会留下
        // 「索引说恢复了、文件还在回收站」的错账。
        File.Move(source, target);
        await RemoveIndexEntryAsync(entry.NoteId, ct).ConfigureAwait(false);

        _logger.LogInformation("便笺已从回收站恢复：{NoteId}。", entry.NoteId);
        return target;
    }

    /// <inheritdoc />
    public async Task PurgeAsync(TrashEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string path = TrashPath(entry.NoteId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await RemoveIndexEntryAsync(entry.NoteId, ct).ConfigureAwait(false);

        _logger.LogInformation("便笺已从回收站彻底删除：{NoteId}。", entry.NoteId);
    }

    /// <inheritdoc />
    public async Task PurgeExpiredAsync(TimeSpan retention, CancellationToken ct = default)
    {
        IReadOnlyList<TrashEntry> entries = await ListAsync(ct).ConfigureAwait(false);
        DateTimeOffset deadline = _clock.Now - retention;

        foreach (TrashEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.DeletedAt < deadline)
            {
                await PurgeAsync(entry, ct).ConfigureAwait(false);
            }
        }
    }

    private string TrashPath(Guid noteId) => Path.Combine(_trashFolder, $"{noteId:N}.lumi");

    /// <summary>
    /// 读索引。文件缺失、形态不认识、读不出来都按「无索引」处理——
    /// 文件才是真源，索引坏了不能让回收站跟着丢东西。
    /// </summary>
    private async Task<Dictionary<Guid, IndexEntry>> ReadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_indexPath);
            TrashIndexFileModel? model = await JsonSerializer
                .DeserializeAsync<TrashIndexFileModel>(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (model is null || model.Version != IndexVersion || model.Entries is null)
            {
                _logger.LogWarning("回收站索引的形态不认识，按无索引处理（文件仍在回收站目录里）。");
                return [];
            }

            var result = new Dictionary<Guid, IndexEntry>(model.Entries.Count);
            foreach (TrashIndexEntry entry in model.Entries)
            {
                result[entry.NoteId] = new IndexEntry(
                    entry.Title ?? "（未知标题）", entry.DeletedAt);
            }

            return result;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                "回收站索引读失败（{ExceptionType}），按无索引处理。", exception.GetType().Name);
            return [];
        }
    }

    private async Task WriteIndexAsync(Dictionary<Guid, IndexEntry> index, CancellationToken ct)
    {
        // 顺手剔除孤儿条目（索引里还记着、但回收站里已经没有对应文件的）。
        var model = new TrashIndexFileModel
        {
            Version = IndexVersion,
            Entries =
            [
                .. index
                    .Where(pair => File.Exists(TrashPath(pair.Key)))
                    .Select(pair => new TrashIndexEntry
                    {
                        NoteId = pair.Key,
                        Title = pair.Value.Title,
                        DeletedAt = pair.Value.DeletedAt,
                    }),
            ],
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        await _writer.WriteAsync(_indexPath, bytes, _clock.Now, ct).ConfigureAwait(false);
    }

    private async Task RemoveIndexEntryAsync(Guid noteId, CancellationToken ct)
    {
        Dictionary<Guid, IndexEntry> index = await ReadIndexAsync(ct).ConfigureAwait(false);
        if (index.Remove(noteId))
        {
            await WriteIndexAsync(index, ct).ConfigureAwait(false);
        }
    }

    private sealed record IndexEntry(string Title, DateTimeOffset DeletedAt);
}
