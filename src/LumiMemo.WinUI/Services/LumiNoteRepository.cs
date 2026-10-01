using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;

namespace LumiMemo.WinUI.Services;

/// <summary>WinUI 富文本实验仓储。一张便笺对应一个原子写入的 .lumi 文件。</summary>
public sealed class LumiNoteRepository : INoteRepository
{
    private readonly string _folder;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly NoteColor _defaultColor;

    public LumiNoteRepository(string folder, IClock clock, AtomicFileWriter writer, NoteColor defaultColor)
    {
        _folder = folder;
        _clock = clock;
        _writer = writer;
        _defaultColor = defaultColor;
    }

    public async Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var notes = new List<Note>();
        foreach (string path in Directory.EnumerateFiles(_folder, "*.lumi", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Note? note = await ReadAsync(path, ct);
                if (note is not null)
                {
                    notes.Add(note);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine($"Cannot open {path}: {exception}");
            }
        }

        return notes;
    }

    public bool IsNoteFile(string path) =>
        string.Equals(Path.GetExtension(path), ".lumi", StringComparison.OrdinalIgnoreCase);

    public Task<NoteFileSync> ReloadAsync(string path, CancellationToken ct = default)
    {
        throw new NotSupportedException("The WinUI rich-text experiment does not yet watch external file edits.");
    }

    public async Task SaveAsync(Note note, CancellationToken ct = default)
    {
        var file = new StoredNote(
            1, note.Id, note.Content, note.RichTextContent, note.Color, [.. note.Tags],
            note.CreatedAt, note.UpdatedAt, note.AutoTitle);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file);
        await _writer.WriteAsync(note.FilePath, bytes, note.UpdatedAt, ct).ConfigureAwait(false);
    }

    public Task<string?> BackupConflictCopyAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        string backup = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.conflict-{_clock.Now:yyyyMMdd-HHmmss}.lumi");
        File.Copy(path, backup, overwrite: false);
        return Task.FromResult<string?>(backup);
    }

    public async Task<Note> CreateAsync(NoteColor? color = null, string? targetFolder = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_folder))
        {
            throw new InvalidOperationException("便笺目录不存在。");
        }

        Guid id = Guid.NewGuid();
        DateTimeOffset now = _clock.Now;
        string folder = targetFolder is null ? _folder : Path.GetFullPath(Path.Combine(_folder, targetFolder));
        if (!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(_folder) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("目标目录必须位于便笺目录内。", nameof(targetFolder));
        }

        var note = new Note
        {
            Id = id,
            FilePath = Path.Combine(folder, $"{id:N}.lumi"),
            Content = string.Empty,
            Color = color ?? _defaultColor,
            CreatedAt = now,
            UpdatedAt = now
        };
        await SaveAsync(note, ct);
        return note;
    }

    private static async Task<Note?> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        StoredNote? stored = await JsonSerializer.DeserializeAsync<StoredNote>(stream, cancellationToken: ct);
        if (stored is null || stored.Version != 1 || stored.Id == Guid.Empty)
        {
            return null;
        }

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
            UpdatedAt = stored.UpdatedAt
        };
    }

    private sealed record StoredNote(
        int Version, Guid Id, string Text, byte[] Rtf, NoteColor Color,
        List<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? AutoTitle = null);
}
