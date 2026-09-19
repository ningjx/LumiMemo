using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="ILayoutStore"/> 替身。
/// </summary>
/// <remarks>
/// 与 <c>LumiMemo.Core.Tests</c> 里那份行为相同，两边<strong>刻意不共享</strong>：
/// 测试工程不互相引用，而为一个几十行的替身建一个共享工程，代价比重复大得多。
/// </remarks>
public sealed class InMemoryLayoutStore : ILayoutStore
{
    private readonly Dictionary<Guid, NoteLayout> _layouts = [];

    /// <summary><see cref="FlushAsync"/> 被调了几次。</summary>
    public int FlushCount { get; private set; }

    /// <summary><see cref="LoadAsync"/> 被调了几次。</summary>
    public int LoadCount { get; private set; }

    /// <summary>有没有未落盘的改动。</summary>
    public bool IsDirty { get; private set; }

    public Task LoadAsync(CancellationToken ct = default)
    {
        LoadCount++;

        return Task.CompletedTask;
    }

    public NoteLayout GetOrCreate(Guid noteId)
    {
        if (_layouts.TryGetValue(noteId, out NoteLayout? existing))
        {
            return existing;
        }

        var created = new NoteLayout { NoteId = noteId };
        _layouts[noteId] = created;

        return created;
    }

    public NoteLayout? TryGet(Guid noteId) => _layouts.GetValueOrDefault(noteId);

    public IReadOnlyCollection<NoteLayout> All => _layouts.Values;

    public void MarkDirty() => IsDirty = true;

    public Task FlushAsync(CancellationToken ct = default)
    {
        FlushCount++;
        IsDirty = false;

        return Task.CompletedTask;
    }
}
