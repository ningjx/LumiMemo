using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// <see cref="INoteStorage"/> 的实现：一张便笺对应一个原子写入的 <c>.lumi</c> 文件。
/// </summary>
/// <remarks>
/// <para>
/// 文件是 JSON（<see cref="StoredNote"/> 的形态）：<c>rtf</c> 为权威内容，
/// <c>text</c> 为纯文本投影（标题、搜索、字数用，下次保存自动修正）。
/// 文件名 <c>{id:N}.lumi</c>，只扫描笔记目录顶层；换标题不重命名文件——
/// 身份在 <c>id</c> 里，不在文件名里。
/// </para>
/// <para>
/// 容错矩阵（一份坏文件不能拖垮启动）：解析失败 / 版本高于当前 / id 为空 → 跳过并记日志；
/// 文件名与 id 不符 → 以 JSON 的 id 为准，不重命名；同 id 重复 → 按路径序取第一个。
/// </para>
/// </remarks>
public sealed class LumiNoteStorage : INoteStorage
{
    /// <summary>当前格式版本。高于它的文件跳过（由更新版本创建）；低于它的走迁移（当前未出现）。</summary>
    public const int FormatVersion = 1;

    private readonly string _folder;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly NoteColor _defaultColor;
    private readonly ILogger<LumiNoteStorage> _logger;

    /// <param name="folder">笔记目录。</param>
    /// <param name="clock">时间来源（不直接用 DateTimeOffset.Now，测试要能钉死时间）。</param>
    /// <param name="writer">原子写盘器。</param>
    /// <param name="defaultColor">新建便笺的默认颜色。</param>
    /// <param name="logger">日志。调用方未接日志设施时传 NullLogger 即可。</param>
    public LumiNoteStorage(
        string folder,
        IClock clock,
        AtomicFileWriter writer,
        NoteColor defaultColor,
        ILogger<LumiNoteStorage> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _folder = folder;
        _clock = clock;
        _writer = writer;
        _defaultColor = defaultColor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        // 上次运行被强杀时留下的临时文件。换名是原子操作，能看到的 .lumitmp 必定是没换成功的残骸。
        int cleaned = AtomicFileWriter.CleanupOrphanedTempFiles(_folder);
        if (cleaned > 0)
        {
            _logger.LogInformation("清理了 {Count} 个残留的临时文件。", cleaned);
        }

        var notes = new List<Note>();
        var seenIds = new HashSet<Guid>();

        // 显式排序：EnumerateFiles 不保证顺序，而「同 id 取第一个」需要确定性。
        foreach (string path in Directory
                     .EnumerateFiles(_folder, "*.lumi", SearchOption.TopDirectoryOnly)
                     .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            Note? note = await TryReadAsync(path, ct).ConfigureAwait(false);
            if (note is null)
            {
                continue;
            }

            if (!seenIds.Add(note.Id))
            {
                _logger.LogWarning("便笺 id 重复，跳过后者：{Path}。", path);
                continue;
            }

            notes.Add(note);
        }

        return [.. notes.OrderBy(static note => note.CreatedAt).ThenBy(static note => note.Id)];
    }

    /// <inheritdoc />
    public async Task SaveAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        var file = new StoredNote(
            FormatVersion,
            note.Id,
            note.Content,
            note.RichTextContent,
            note.Color,
            [.. note.Tags],
            note.CreatedAt,
            note.UpdatedAt,
            note.AutoTitle);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file);

        await _writer.WriteAsync(note.FilePath, bytes, note.UpdatedAt, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Note> CreateAsync(NoteColor? color = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(_folder))
        {
            throw new InvalidOperationException("便笺目录不存在。");
        }

        Guid id = Guid.NewGuid();
        DateTimeOffset now = _clock.Now;

        var note = new Note
        {
            Id = id,
            FilePath = Path.Combine(_folder, $"{id:N}.lumi"),
            Content = string.Empty,
            Color = color ?? _defaultColor,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await SaveAsync(note, ct).ConfigureAwait(false);

        return note;
    }

    /// <summary>读一个文件；坏文件返回 <see langword="null"/> 并记日志，不抛出。</summary>
    private async Task<Note?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            StoredNote? stored = await JsonSerializer
                .DeserializeAsync<StoredNote>(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (stored is null || stored.Id == Guid.Empty)
            {
                _logger.LogWarning("跳过格式不完整的便笺：{Path}。", path);
                return null;
            }

            if (stored.Version > FormatVersion)
            {
                _logger.LogWarning(
                    "便笺由更新版本的 LumiMemo 创建，已跳过：{Path}（版本 {Version}）。",
                    path,
                    stored.Version);
                return null;
            }

            // stored.Version < FormatVersion 的迁移入口留在这里（当前没有更旧的版本）。

            return new Note
            {
                Id = stored.Id,
                FilePath = path,
                Content = stored.Text ?? string.Empty,
                AutoTitle = stored.AutoTitle,
                RichTextContent = stored.Rtf ?? [],
                Color = stored.Color,
                Tags = stored.Tags ?? [],
                CreatedAt = stored.CreatedAt,
                UpdatedAt = stored.UpdatedAt,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 只记异常类型名不记异常对象：消息可能内嵌文件内容片段（§19.5）。
            _logger.LogWarning(
                "无法读取便笺，已跳过：{Path}（{ExceptionType}）。",
                path,
                exception.GetType().Name);
            return null;
        }
    }

    /// <summary>磁盘上的文件形态。字段名即格式契约，改名等于升 <see cref="FormatVersion"/>。</summary>
    /// <remarks>
    /// 三个引用型字段声明为可空：文件可能被手改缺字段，读取时按「缺失即空」降级，
    /// 而不是让整个文件解析失败。
    /// </remarks>
    private sealed record StoredNote(
        int Version,
        Guid Id,
        string? Text,
        byte[]? Rtf,
        NoteColor Color,
        List<string>? Tags,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? AutoTitle = null);
}
