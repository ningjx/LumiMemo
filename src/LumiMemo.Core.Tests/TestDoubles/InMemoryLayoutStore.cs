using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="ILayoutStore"/> 替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 真实现（<c>JsonLayoutStore</c>）的磁盘行为已经在 <c>LumiMemo.Integration.Tests</c> 里
/// 用真实文件系统测过，那份不必在这里重来。本替身只服务一件事：
/// 数清楚 <see cref="FlushAsync"/> 究竟被调了几次——节流合并是 <c>LayoutService</c>
/// 的逻辑，而它的全部证据就是「落盘了几次」。
/// </para>
/// <para>
/// <strong>刻意不做真实现那个「不脏就直接返回」的短路。</strong> 带上它的话，
/// 第一次落盘之后所有调用都会被吃掉，于是「连调 5 次只写 1 次」这条断言哪怕节流
/// 完全失效也照样通过，测试就成了一句废话。本类的计数必须是<strong>调用次数</strong>。
/// </para>
/// </remarks>
public sealed class InMemoryLayoutStore : ILayoutStore
{
    private readonly Dictionary<Guid, NoteLayout> _layouts = [];

    /// <summary><see cref="FlushAsync"/> 被调了几次。</summary>
    public int FlushCount { get; private set; }

    /// <summary><see cref="LoadAsync"/> 被调了几次。启动序列「恰好载入一次」靠它验。</summary>
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
