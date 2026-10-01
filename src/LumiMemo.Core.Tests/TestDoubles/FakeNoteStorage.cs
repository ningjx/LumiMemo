using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="INoteStorage"/> 替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 真实的 .lumi 读写（原子写盘、容错矩阵、版本策略）已经在
/// <c>LumiMemo.Integration.Tests</c> 里用真实文件系统测过，那份不必在这里重来。
/// 本替身只服务编排逻辑：<strong>谁在什么时候把什么东西交给了存储</strong>。
/// </para>
/// <para>
/// 刻意<strong>不</strong>做任何业务加工——不校验内容、不补字段。
/// 一旦替身开始「帮忙」，被测代码的缺陷就会被替身悄悄掩盖过去。
/// </para>
/// </remarks>
public sealed class FakeNoteStorage : INoteStorage
{
    /// <summary><see cref="LoadAllAsync"/> 要返回的便签，由用例预先放好。</summary>
    public List<Note> NotesToLoad { get; } = [];

    /// <summary>所有交给 <see cref="SaveAsync"/> 的便签，按发生顺序。</summary>
    public List<Note> Saved { get; } = [];

    /// <summary><see cref="LoadAllAsync"/> 被调了几次。</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>所有交给 <see cref="CreateAsync"/> 的颜色参数，按发生顺序。</summary>
    public List<NoteColor?> CreateRequests { get; } = [];

    /// <summary><see cref="CreateAsync"/> 要交出去的便签；不设时自己造一张。</summary>
    public Func<Note>? CreateHandler { get; set; }

    /// <summary><see cref="CreateAsync"/> 要抛的异常；不设时正常返回。</summary>
    public Exception? CreateException { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        LoadCallCount++;

        return Task.FromResult<IReadOnlyList<Note>>(NotesToLoad);
    }

    /// <inheritdoc />
    public Task SaveAsync(Note note, CancellationToken ct = default)
    {
        Saved.Add(note);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Note> CreateAsync(NoteColor? color = null, CancellationToken ct = default)
    {
        CreateRequests.Add(color);

        if (CreateException is { } exception)
        {
            return Task.FromException<Note>(exception);
        }

        return Task.FromResult(CreateHandler is { } handler ? handler() : NewNote());
    }

    private static Note NewNote()
    {
        var id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.lumi",
            Content = string.Empty,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }
}
