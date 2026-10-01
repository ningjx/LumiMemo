using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>不出文件的 <see cref="ILayoutStore"/> 替身。</summary>
public sealed class InMemoryLayoutStore : ILayoutStore
{
    private readonly Dictionary<Guid, NoteLayout> _layouts = [];

    /// <summary><see cref="MarkDirty"/> 被调了几次。</summary>
    public int MarkDirtyCount { get; private set; }

    /// <summary><see cref="FlushAsync"/> 被调了几次。</summary>
    public int FlushCount { get; private set; }

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public NoteLayout GetOrCreate(Guid noteId)
    {
        if (!_layouts.TryGetValue(noteId, out NoteLayout? layout))
        {
            layout = new NoteLayout { NoteId = noteId };
            _layouts[noteId] = layout;
        }

        return layout;
    }

    public NoteLayout? TryGet(Guid noteId) => _layouts.GetValueOrDefault(noteId);

    public IReadOnlyCollection<NoteLayout> All => _layouts.Values;

    public void MarkDirty() => MarkDirtyCount++;

    public Task FlushAsync(CancellationToken ct = default)
    {
        FlushCount++;

        return Task.CompletedTask;
    }
}
