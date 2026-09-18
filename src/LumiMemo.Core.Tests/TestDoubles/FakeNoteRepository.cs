using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="INoteRepository"/> 替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 真实仓储的磁盘行为（原子写盘、编码与行尾的原样保留、id 回补）已经在
/// <c>LumiMemo.Integration.Tests</c> 里用真实文件系统测过，那份不必在这里重来。
/// 本替身只服务 <c>NoteService</c> 的编排逻辑：<strong>它有没有把对的东西在对的时候交出去</strong>。
/// </para>
/// <para>
/// 刻意<strong>不</strong>做任何业务加工——不补 id、不改目录、不校验内容。
/// 一旦替身开始「帮忙」，被测代码的缺陷就会被替身悄悄掩盖过去。
/// </para>
/// </remarks>
public sealed class FakeNoteRepository : INoteRepository
{
    /// <summary><see cref="LoadAllAsync"/> 要返回的便签，由用例预先放好。</summary>
    public List<Note> NotesToLoad { get; } = [];

    /// <summary>所有交给 <see cref="SaveAsync"/> 的便签，按发生顺序。</summary>
    public List<Note> Saved { get; } = [];

    /// <summary><see cref="LoadAllAsync"/> 被调了几次。</summary>
    public int LoadCallCount { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        LoadCallCount++;

        return Task.FromResult<IReadOnlyList<Note>>(NotesToLoad);
    }

    /// <inheritdoc />
    public Task<Note?> ReloadAsync(string path, CancellationToken ct = default)
    {
        Note? found = NotesToLoad.Find(
            note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task SaveAsync(Note note, CancellationToken ct = default)
    {
        Saved.Add(note);

        return Task.CompletedTask;
    }
}
