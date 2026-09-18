using LumiMemo.Core.Models;

namespace LumiMemo.Core.Stores;

/// <summary>
/// 运行时的便签内存状态（§9.1、<c>Core.Stores</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它不是数据库，也不做 IO，更不发事件</strong>。Store 只负责状态；
/// 「改了一半就通知」是必须避免的，所以事件由 <c>NoteService</c> 在完成一次完整操作后统一发出（§9.1）。
/// </para>
/// <para>
/// <strong>所有写入方法只能在 UI 线程调用</strong>（§3.4 规则 T1）。唯一的例外路径是
/// <c>AtomicFileWriter</c> 在后台线程写完文件后，用 <c>Dispatcher.InvokeAsync</c>
/// 回到 UI 线程再更新这里。
/// </para>
/// <para>
/// Store 里持有的是 <see cref="Note"/> 的<strong>唯一实例</strong>，ViewModel 拿到的是同一个引用，
/// 因此不存在「两份数据」问题（§18.4）。注意这意味着<strong>不要</strong>在 Store 里存放
/// <c>Note</c> 的副本。
/// </para>
/// </remarks>
public sealed class NoteStore
{
    private readonly Dictionary<Guid, Note> _notes = [];

    /// <summary>
    /// 排序后的快照缓存（§9.1、§3.4 规则 T2）。
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="Snapshot"/> 被调用且数据已变时重建，而不是每次增删改都重建——
    /// 后者在批量载入 2000 张便签时会退化成 O(n² log n)。
    /// </remarks>
    private Note[]? _orderedCache;

    /// <summary>取便签，不存在时返回 <c>null</c>。</summary>
    public Note? TryGet(Guid id) => _notes.GetValueOrDefault(id);

    /// <summary>
    /// 返回全部便签的只读快照（§9.1）。
    /// </summary>
    /// <remarks>
    /// 调用方可以安全遍历：Store 在别处的增删不会影响本次拿到的数组，
    /// 也不会抛 <c>Collection was modified</c>。排序按 <see cref="Note.Id"/> 升序，
    /// 保证同一份数据每次得到相同顺序（§21.2 对确定性的要求）。
    /// <strong>管理器展示用的排序是另一回事</strong>，由搜索服务按 §12.2 处理。
    /// </remarks>
    public IReadOnlyList<Note> Snapshot()
    {
        _orderedCache ??= [.. _notes.Values.OrderBy(static n => n.Id)];

        return _orderedCache;
    }

    public bool Contains(Guid id) => _notes.ContainsKey(id);

    /// <summary>便签数量。</summary>
    public int Count => _notes.Count;

    /// <summary>加入一张便签。同 id 已存在时覆盖。</summary>
    public void Add(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        _notes[note.Id] = note;
        _orderedCache = null;
    }

    /// <summary>
    /// 更新一张便签（§9.1）。
    /// </summary>
    /// <remarks>
    /// 传入的必须是 Store 里那个实例（或同 id 的新实例）。本方法只负责把实例放进字典——
    /// 改正文导致的标题缓存失效由 <see cref="Note.Content"/> 的 setter 自己处理，
    /// 这里不需要（也不应该）操心。
    /// </remarks>
    public void Update(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        _notes[note.Id] = note;
        _orderedCache = null;
    }

    public void Remove(Guid id)
    {
        if (_notes.Remove(id))
        {
            _orderedCache = null;
        }
    }

    public void Clear()
    {
        _notes.Clear();
        _orderedCache = null;
    }
}
