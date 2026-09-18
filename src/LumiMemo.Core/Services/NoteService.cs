using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Stores;

namespace LumiMemo.Core.Services;

/// <summary>
/// <see cref="INoteService"/> 的实现：便签业务的唯一入口（§14.2）。
/// </summary>
/// <remarks>
/// <para>
/// 本类只编排<strong>内存状态</strong>与<strong>调用顺序</strong>，三件事都不做：
/// 不碰窗口（那是 App 层的 <c>WindowManager</c>）、不碰坐标（那是 <see cref="LayoutService"/>）、
/// 不做文件解析（那是 Infrastructure 的仓储）。
/// </para>
/// <para>
/// <strong>不注入 <c>ILogger</c></strong>：<c>LumiMemo.Core</c> 零第三方依赖（§4.1）。
/// 真正值得记录的事件（解析降级、id 回补、文件被占用、备份失败）全部发生在
/// Infrastructure 的仓储层，那里有完整的日志设施。本层没有新的失败模式需要记录——
/// 它做的只是把已经成功的结果搬进内存。
/// </para>
/// </remarks>
public sealed class NoteService : INoteService
{
    private readonly NoteStore _store;
    private readonly SearchIndex _index;
    private readonly INoteRepository _repository;
    private readonly LayoutService _layout;
    private readonly IClock _clock;

    public NoteService(
        NoteStore store,
        SearchIndex index,
        INoteRepository repository,
        LayoutService layout,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _index = index;
        _repository = repository;
        _layout = layout;
        _clock = clock;
    }

    // ---- 磁盘 → 内存 ----

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 本方法里的 <c>await</c> <strong>刻意不加 <c>ConfigureAwait(false)</c></strong>：
    /// 续体必须回到 UI 线程，因为紧接着要写 <see cref="NoteStore"/> 与
    /// <see cref="SearchIndex"/>，而两者都只能在 UI 线程上改（§3.4 规则 T1、T5）。
    /// </para>
    /// <para>
    /// 这是「让 Core 不必认识 <c>IDispatcher</c>」的关键约定。注意 <c>.editorconfig</c>
    /// 已把 <c>CA2007</c> 设为 <c>none</c>，所以这里不加不会报警——那种沉默反而危险，
    /// 后来的人很容易顺手补上一个 <c>ConfigureAwait(false)</c> 而看不出任何异样，
    /// 直到某次冷启动真的撞上跨线程异常。
    /// </para>
    /// </remarks>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        // §5.5 的前三步（解析、补 id、处理冲突）全在仓储层完成，本层只负责最后一步：建 Store。
        IReadOnlyList<Note> notes = await _repository.LoadAllAsync(ct);

        _store.Clear();

        foreach (var note in notes)
        {
            _store.Add(note);
        }

        _index.Rebuild(_store.Snapshot());
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含文件监听（§10 推迟）。</exception>
    /// <remarks>
    /// 刻意<strong>抛异常而不是留一个空实现</strong>：文件监听的整套机制（去抖、内容哈希比对、
    /// 自写抑制窗口）本轮都没做，而本程序的启动扫描会主动写用户文件（补 id、原子替换）。
    /// 一个有监听器却带着空实现的 <c>ApplyExternalChange</c>，会让那些自写事件以「外部修改」
    /// 的身份涌进内存，把用户刚改的内容覆盖回去。宁可让它在调用点立刻炸掉。
    /// </remarks>
    public void ApplyExternalChange(string path, NoteReadResult readResult) =>
        throw new NotSupportedException(
            "文件监听推迟到后续阶段（§10）。当前版本不感知运行期间的外部文件修改。");

    // ---- 编辑 → 内存 ----

    /// <inheritdoc />
    /// <remarks>
    /// 同时更新纯文本索引与 <see cref="Note.UpdatedAt"/>。后者在这里改而不是等到落盘时改，
    /// 是因为管理器的「最近更新」排序读的就是它：等 500 毫秒的去抖结束才刷新，
    /// 列表看起来会「慢半拍」。它还决定了文件在磁盘上的修改时间——仓储写盘时用它。
    /// </remarks>
    public void ApplyLocalEdit(Note note, string content)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(content);

        note.Content = content;
        note.UpdatedAt = _clock.Now;

        _index.OnNoteUpdated(note);
    }

    // ---- 内存 → 磁盘 ----

    /// <inheritdoc />
    /// <remarks>
    /// 便签不存在时静默返回：调用方（自动保存）是按 id 排的队，而用户完全可能在
    /// 去抖那 500 毫秒里把这张便签删掉。那不是错误，不该抛异常。
    /// </remarks>
    public Task SaveNoteAsync(Guid noteId)
    {
        Note? note = _store.TryGet(noteId);

        return note is null ? Task.CompletedTask : _repository.SaveAsync(note, CancellationToken.None);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 退出流程用（§17.4）。逐个保存而不是并行：写盘要抢文件锁，并行只会让它们互相重试。
    /// </remarks>
    public async Task SaveAllAsync(CancellationToken ct = default)
    {
        foreach (var note in _store.Snapshot())
        {
            ct.ThrowIfCancellationRequested();

            await _repository.SaveAsync(note, ct);
        }
    }

    // ---- 业务操作 ----

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含新建便签。</exception>
    public Task<Note> CreateNoteAsync(NoteColor? color = null, string? targetFolder = null) =>
        throw new NotSupportedException("新建便签尚未接入（需要文件名分配与首次写盘）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含移动便签。</exception>
    public Task MoveNoteAsync(Guid noteId, string targetFolder) =>
        throw new NotSupportedException("移动便签尚未接入（需要重写正文里的附件相对链接）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含回收站。</exception>
    public Task DeleteNoteAsync(Guid noteId) =>
        throw new NotSupportedException("删除便签尚未接入（必须先有回收站，不能直接删文件）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含回收站。</exception>
    public Task RestoreFromTrashAsync(Guid noteId, string? targetPath) =>
        throw new NotSupportedException("从回收站恢复尚未接入。");

    // ---- 窗口开关 ----

    /// <inheritdoc />
    /// <remarks>
    /// 取布局用 <see cref="LayoutService.GetOrCreate"/>：一张从未打开过的便签在这里
    /// 才第一次拿到自己的布局条目。它同时会安排一次落盘，这正是 §8.3 要求的
    /// 「新条目必须写盘」——否则下次启动无从知道这张便签上次是开着的。
    /// </remarks>
    public NoteOpenRequest? OpenNote(Guid noteId)
    {
        Note? note = _store.TryGet(noteId);

        if (note is null)
        {
            return null;
        }

        NoteLayout layout = _layout.GetOrCreate(noteId);
        layout.IsOpen = true;
        _layout.MarkDirtyAndScheduleFlush();

        return new NoteOpenRequest(note, layout);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只返回 <c>IsOpen</c> 为 true 的便签，且<strong>不产生副作用</strong>：
    /// 与 <see cref="OpenNote"/> 不同，这里不调 <c>GetOrCreate</c>。
    /// 一张没有布局条目的便签意味着它从未被打开过，按 §17.1 的裁决（首启不自动弹窗）
    /// 它就该保持关闭。
    /// </remarks>
    public IReadOnlyList<NoteOpenRequest> OpenAll()
    {
        var requests = new List<NoteOpenRequest>();

        foreach (var note in _store.Snapshot())
        {
            NoteLayout? layout = _layout.TryGet(note.Id);

            if (layout is { IsOpen: true })
            {
                requests.Add(new NoteOpenRequest(note, layout));
            }
        }

        return requests;
    }

    /// <inheritdoc />
    public void MarkNoteClosed(Guid noteId)
    {
        NoteLayout? layout = _layout.TryGet(noteId);

        if (layout is null)
        {
            return;
        }

        layout.IsOpen = false;
        _layout.MarkDirtyAndScheduleFlush();
    }
}
