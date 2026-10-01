using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>不出内存的 <see cref="INoteStorage"/> 替身。</summary>
/// <remarks>
/// 真实 .lumi 读写由 Integration.Tests 打真实文件系统覆盖；本替身只服务编排逻辑，
/// 并提供 <see cref="SaveException"/> 注入失败路径。
/// </remarks>
public sealed class FakeNoteStorage : INoteStorage
{
    /// <summary><see cref="LoadAllAsync"/> 要返回的便签。</summary>
    public List<Note> NotesToLoad { get; } = [];

    /// <summary>成功传给 <see cref="SaveAsync"/> 的便签，按发生顺序。</summary>
    public List<Note> Saved { get; } = [];

    /// <summary>设了就让每次 <see cref="SaveAsync"/> 抛它。</summary>
    public Exception? SaveException { get; set; }

    /// <summary>设了就让每次 <see cref="SaveAsync"/> 先等它完成（模拟慢盘）。</summary>
    public Task? SaveGate { get; set; }

    public int LoadCallCount { get; private set; }

    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        LoadCallCount++;

        return Task.FromResult<IReadOnlyList<Note>>(NotesToLoad);
    }

    public Task<Note?> TryLoadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(NotesToLoad.Find(
            note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase)));

    public async Task SaveAsync(Note note, CancellationToken ct = default)
    {
        if (SaveGate is { } gate)
        {
            await gate;
        }

        if (SaveException is { } exception)
        {
            throw exception;
        }

        Saved.Add(note);
    }

    public Task<Note> CreateAsync(NoteColor? color = null, CancellationToken ct = default)
    {
        Guid id = Guid.NewGuid();

        return Task.FromResult(new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.lumi",
            Content = string.Empty,
            Color = color ?? NoteColor.Yellow,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
    }

    /// <summary>所有交给 <see cref="DuplicateAsync"/> 的源便签，按发生顺序。</summary>
    public List<Note> Duplicated { get; } = [];

    public Task<Note> DuplicateAsync(Note source, CancellationToken ct = default)
    {
        Duplicated.Add(source);

        Guid id = Guid.NewGuid();

        return Task.FromResult(new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.lumi",
            Content = source.Content,
            RichTextContent = [.. source.RichTextContent],
            Color = source.Color,
            Tags = [.. source.Tags],
            AutoTitle = source.AutoTitle,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
    }
}
