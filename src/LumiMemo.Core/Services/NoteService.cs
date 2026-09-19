using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Stores;

namespace LumiMemo.Core.Services;

/// <summary>
/// <see cref="INoteService"/> 的实现：便签业务的唯一入口（§14.2）。
/// </summary>
/// <remarks>
/// <para>
/// 本类只编排<strong>内存状态</strong>与<strong>调用顺序</strong>，四件事都不做：
/// 不碰窗口（那是 App 层的 <c>WindowManager</c>）、不碰坐标（那是 <see cref="LayoutService"/>）、
/// 不做文件解析（那是 Infrastructure 的仓储）、不搬回收站里的文件（那是 <see cref="TrashService"/>）。
/// 删除与恢复因此只是一行转发——留着这个入口是为了让调用方按「便签 id」办事，
/// 而不必认识回收站目录与索引。
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
    private readonly TrashService _trash;
    private readonly IClock _clock;

    /// <summary>
    /// 本地编辑的序号，按便签 id 记。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="_savedVersions"/> 配对使用：两个号对不上，就说明这张便签有过
    /// <strong>没落盘的改动</strong>。这个信号是 §11.4 三路比较里「本地」那一格，
    /// 而它只有本类拿得到——<c>NoteViewModel.IsDirty</c> 是一个从来没人写过的死字段，
    /// <c>AutoSaveService</c> 手里的「排队中」也不等于「与磁盘不同」（保存成功后队列才空）。
    /// </remarks>
    private readonly Dictionary<Guid, int> _editVersions = [];

    /// <summary>已经确认落到磁盘上的那个版本号，按便签 id 记。</summary>
    private readonly Dictionary<Guid, int> _savedVersions = [];

    public NoteService(
        NoteStore store,
        SearchIndex index,
        INoteRepository repository,
        LayoutService layout,
        TrashService trash,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(trash);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _index = index;
        _repository = repository;
        _layout = layout;
        _trash = trash;
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
    public async Task<IReadOnlyList<ExternalChangeResult>> LoadAllAsync(CancellationToken ct = default)
    {
        // §5.5 的前三步（解析、补 id、处理冲突）全在仓储层完成，本层只负责最后一步：建 Store。
        IReadOnlyList<Note> notes = await _repository.LoadAllAsync(ct);

        var changes = new List<ExternalChangeResult>();
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Note disk in notes)
        {
            onDisk.Add(disk.FilePath);

            Note? local = _store.TryGetByPath(disk.FilePath);

            // 重扫没有「上次同步的字节」可用——这一次扫描自己就把仓储里那份基线刷新了。
            // 于是只能比内容：用户数据不一样，或者路径上换了身份（id 变了），都算变过。
            bool changed = local is null
                || local.Id != disk.Id
                || !local.HasSameUserDataAs(disk);

            Record(changes, Merge(disk.FilePath, disk, changed));
        }

        // 第二趟找「内存里有、磁盘上没有了」的。必须先取快照：合并会改 Store，
        // 而快照是数组，遍历时不受后面的增删影响（NoteStore.Snapshot 的约定）。
        foreach (Note local in _store.Snapshot())
        {
            if (!onDisk.Contains(local.FilePath))
            {
                Record(changes, Merge(local.FilePath, disk: null, diskChanged: true));
            }
        }

        // 索引不需要 Rebuild：下面每一处增删改都顺手更新了它，没变的那部分本来就在里面。
        return changes;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 真正做判定的是 <see cref="Merge"/>——它与整目录重扫共用同一段代码，
    /// 于是「用户点托盘上的重新加载全部便签」与「外面有人改了文件」对待未落盘改动的方式
    /// 必然一致，不会出现一条路静默覆盖、另一条路弹对话框这种半对半错的状态。
    /// </remarks>
    public ExternalChangeResult ApplyExternalChange(string path, NoteFileSync fileSync)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fileSync);

        return Merge(path, fileSync.Note, fileSync.DiskChanged);
    }

    /// <inheritdoc />
    public void ResolveConflictByReload(ExternalChangeResult conflict)
    {
        (Note local, Note disk) = SplitConflict(conflict);

        local.CopyFrom(disk);
        _index.OnNoteUpdated(local);
        MarkSynced(local.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 备份那一步是<strong>故意让异常往外走</strong>的：副本写不出来（没有权限、磁盘满）
    /// 就不该把磁盘上那一版覆盖掉，而「没覆盖」这件事必须让调用方知道——它要告诉用户
    /// 「这次覆盖没做成，你的改动还在便签里」。用户点这一档时心里想的是「我这份才是对的」，
    /// 但万一他想错了，那份副本是他唯一的退路。
    /// </remarks>
    public async Task ResolveConflictByOverwriteAsync(
        ExternalChangeResult conflict,
        CancellationToken ct = default)
    {
        (Note local, _) = SplitConflict(conflict);

        // 文件已经不在了时返回 null——那不是失败，是没有东西可留底，照写不误。
        _ = await _repository.BackupConflictCopyAsync(local.FilePath, ct);

        await _repository.SaveAsync(local, ct).ConfigureAwait(false);

        MarkSynced(local.Id);
    }

    /// <summary>
    /// 三路比较的公共落点：把磁盘上这一份合并进内存，返回这次变化是什么（§10.2、§11.4）。
    /// </summary>
    /// <param name="path">文件路径，用来在 <see cref="NoteStore"/> 里定位便签。</param>
    /// <param name="disk">磁盘上那一份；文件已经不在时是 <see langword="null"/>。</param>
    /// <param name="diskChanged">
    /// 磁盘相对「本程序上次同步这个文件」是否变过。为 <see langword="false"/> 时直接忽略，
    /// 自写事件与重复通知都落在这一档。
    /// </param>
    /// <remarks>
    /// 判定顺序不能换：先排除「磁盘没变」，再排除「内容本来就一样」，
    /// <strong>最后才看本地有没有未落盘的改动</strong>。把冲突判定提前的话，
    /// 「用户把改过的字又删回原样、而磁盘上有人碰过这个文件」也会弹出对话框。
    /// </remarks>
    private ExternalChangeResult Merge(string path, Note? disk, bool diskChanged)
    {
        Note? local = _store.TryGetByPath(path);
        var sync = new NoteFileSync(disk, diskChanged);

        if (!diskChanged)
        {
            return new ExternalChangeResult(ExternalChangeKind.None, local, sync);
        }

        if (disk is null)
        {
            if (local is null)
            {
                // 文件没了，而内存里本来也没有它——没有要摘的东西。
                return new ExternalChangeResult(ExternalChangeKind.None, null, sync);
            }

            Detach(local);
            return new ExternalChangeResult(ExternalChangeKind.Deleted, local, sync);
        }

        if (local is null)
        {
            // 这个路径上没有便签，但那个 id 在内存里已经有了：文件被外部重命名或搬了位置。
            // 身份没变，所以这不是「多出来一张便签」，而是同一张便签换了地方——
            // 把字段搬进原实例（连带 FilePath），窗口绑着的引用因此不会变成孤儿。
            // 将来本程序的「移动便签」也走这一条。
            if (_store.TryGet(disk.Id) is { } moved)
            {
                moved.CopyFrom(disk);
                _store.Update(moved);
                _index.OnNoteUpdated(moved);
                MarkSynced(moved.Id);

                return new ExternalChangeResult(ExternalChangeKind.Reloaded, moved, sync);
            }

            _store.Add(disk);
            _index.OnNoteAdded(disk);
            MarkSynced(disk.Id);

            return new ExternalChangeResult(ExternalChangeKind.Created, disk, sync);
        }

        if (local.Id != disk.Id)
        {
            // 同一个路径换了身份：外部编辑器改写了 Front Matter 里的 id，或者文件被整个换掉。
            // 文件是权威（§5.5：id 来自 Front Matter），所以旧的摘掉、新的放进来，
            // 摘掉的那一张放进 Replaced——调用方要先关掉它那扇窗口。
            Detach(local);
            _store.Add(disk);
            _index.OnNoteAdded(disk);
            MarkSynced(disk.Id);

            return new ExternalChangeResult(ExternalChangeKind.Created, disk, sync, local);
        }

        if (local.HasSameUserDataAs(disk))
        {
            // 内容本来就一样，只是文件的形态被别的程序碰过。把磁盘版的形态字段接过来，
            // 正文与用户数据一个字节都不会变——但重载本身要让界面知道（时间戳会动）。
            local.CopyFrom(disk);
            MarkSynced(local.Id);

            return new ExternalChangeResult(ExternalChangeKind.Reloaded, local, sync);
        }

        if (HasUnsavedEdit(local.Id))
        {
            // 两边都改了、而且改得不一样：真冲突（§11.4）。停下来交给调用方去问用户。
            return new ExternalChangeResult(ExternalChangeKind.Conflict, local, sync);
        }

        local.CopyFrom(disk);
        _index.OnNoteUpdated(local);
        MarkSynced(local.Id);

        return new ExternalChangeResult(ExternalChangeKind.Reloaded, local, sync);
    }

    /// <summary>把一张便签从内存里摘干净：Store、索引、以及它那两个版本号。</summary>
    private void Detach(Note note)
    {
        _store.Remove(note.Id);
        _index.OnNoteRemoved(note.Id);
        Forget(note.Id);
    }

    /// <summary>只把有变化的记进结果里；<see cref="ExternalChangeKind.None"/> 不是变化。</summary>
    private static void Record(List<ExternalChangeResult> changes, ExternalChangeResult result)
    {
        if (result.Kind != ExternalChangeKind.None)
        {
            changes.Add(result);
        }
    }

    /// <summary>从一次冲突里取出两边；参数不是冲突结论时抛异常（那是调用方的编程错误）。</summary>
    private static (Note Local, Note Disk) SplitConflict(ExternalChangeResult conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        if (conflict.Kind != ExternalChangeKind.Conflict)
        {
            throw new InvalidOperationException(
                $"只有 {nameof(ExternalChangeKind.Conflict)} 结论才需要用户裁决，实际拿到的是 {conflict.Kind}。");
        }

        // 两边都非空是 Conflict 这个结论自带的：一边没有内容就不叫冲突了。
        return (conflict.LocalNote!, conflict.Disk.Note!);
    }

    // ---- 「有没有未落盘的改动」----

    /// <summary>记一次本地编辑。</summary>
    private void MarkEdited(Guid noteId) =>
        _editVersions[noteId] = _editVersions.GetValueOrDefault(noteId) + 1;

    /// <summary>这张便签有没有还没落盘的改动（§11.4 三路比较里的「本地」那一格）。</summary>
    private bool HasUnsavedEdit(Guid noteId) =>
        _editVersions.GetValueOrDefault(noteId) != _savedVersions.GetValueOrDefault(noteId);

    /// <summary>把「当前这一版」记成已经落盘，于是这张便签重新变干净。</summary>
    private void MarkSynced(Guid noteId) =>
        _savedVersions[noteId] = _editVersions.GetValueOrDefault(noteId);

    /// <summary>便签没了，它那两个版本号也就不必留着。</summary>
    private void Forget(Guid noteId)
    {
        _editVersions.Remove(noteId);
        _savedVersions.Remove(noteId);
    }

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
        MarkEdited(note.Id);

        _index.OnNoteUpdated(note);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>不必动索引</strong>：<see cref="SearchIndex"/> 里只有正文的纯文本与标签，
    /// 颜色不在其中。另外两个字段（<c>Content</c> / <c>Tags</c>）都改了索引可见的东西，
    /// 所以它们那一侧要刷新——这里不要，别为了「三处长得一样」硬补一次
    /// <c>OnNoteUpdated</c>，那会让人以为颜色也在索引里。
    /// </remarks>
    public void ApplyColorEdit(Note note, NoteColor color)
    {
        ArgumentNullException.ThrowIfNull(note);

        note.Color = color;
        note.UpdatedAt = _clock.Now;
        MarkEdited(note.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 逐个 <see cref="TagRules.TryAdd"/> 到新列表再整体换上去，而不是
    /// <c>tags.Select(Normalize).Distinct()</c>：去重的判据是<strong>忽略大小写但保留首次写法</strong>
    /// （§5.8），<c>Distinct()</c> 的默认比较器按字节序挑，用户写的 <c>Work</c> 可能被判成重复
    /// 而以 <c>work</c> 落盘。
    /// </remarks>
    public void ApplyTagsEdit(Note note, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(tags);

        var normalized = new List<string>(tags.Count);

        foreach (string tag in tags)
        {
            _ = TagRules.TryAdd(normalized, tag);
        }

        // 原地换内容而不是 note.Tags = normalized：Note.Tags 的引用已经散出去了
        // （NoteListItem 直接交出这个列表本身），换掉整个对象会让那些引用指向旧数据。
        note.Tags.Clear();
        note.Tags.AddRange(normalized);
        note.UpdatedAt = _clock.Now;
        MarkEdited(note.Id);

        // 标签在索引里有单独一份（SearchIndex 的 byTag），必须刷新，否则
        // 「搜到的便签已经没有这个标签了」（§12.1 的标签命中）。
        _index.OnNoteUpdated(note);
    }

    // ---- 内存 → 磁盘 ----

    /// <inheritdoc />
    /// <remarks>
    /// 便签不存在时静默返回：调用方（自动保存）是按 id 排的队，而用户完全可能在
    /// 去抖那 500 毫秒里把这张便签删掉。那不是错误，不该抛异常。
    /// </remarks>
    public async Task SaveNoteAsync(Guid noteId)
    {
        Note? note = _store.TryGet(noteId);

        if (note is not null)
        {
            await SaveAndMarkSyncedAsync(note, CancellationToken.None);
        }
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

            await SaveAndMarkSyncedAsync(note, ct);
        }
    }

    /// <summary>写一张便签，并在写成功后把它记成「已落盘」。</summary>
    /// <remarks>
    /// <para>
    /// 版本号要在 <c>await</c> <strong>之前</strong>取：写盘是 IO，这中间用户完全可能继续敲字。
    /// 保存结束时号没动，才谈得上「这一版已经在磁盘上了」；号动了说明最后那几笔还没写出去，
    /// 不能一起标成干净——标错的代价是下一次外部修改被当成「本地没有改动」而静默覆盖掉。
    /// </para>
    /// <para>
    /// 写失败时<strong>不标已落盘</strong>：异常直接往外走，磁盘上那份依旧落后于内存。
    /// </para>
    /// </remarks>
    private async Task SaveAndMarkSyncedAsync(Note note, CancellationToken ct)
    {
        int version = _editVersions.GetValueOrDefault(note.Id);

        await _repository.SaveAsync(note, ct);

        if (_editVersions.GetValueOrDefault(note.Id) == version)
        {
            MarkSynced(note.Id);
        }
    }

    // ---- 业务操作 ----

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 只做编排：文件名的分配、首次写盘、编码档的登记全在
    /// <see cref="INoteRepository.CreateAsync"/> 里。Core 不碰文件系统（§3.1）。
    /// </para>
    /// <para>
    /// <strong>不建布局条目</strong>。布局是设备状态，谁开窗谁负责（§8.3）——
    /// 管理器走 <c>LayoutService.GetOrCreate</c>，<c>OpenNote</c> 那条路也是各自管各自的。
    /// 在这里顺手建一条，会让「只是新建、还没决定开不开窗」的情形多出一条布局垃圾。
    /// </para>
    /// </remarks>
    public async Task<Note> CreateNoteAsync(NoteColor? color = null, string? targetFolder = null)
    {
        Note note = await _repository.CreateAsync(color, targetFolder).ConfigureAwait(true);

        _store.Add(note);
        _index.OnNoteAdded(note);

        return note;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">本轮不含移动便签。</exception>
    public Task MoveNoteAsync(Guid noteId, string targetFolder) =>
        throw new NotSupportedException("移动便签尚未接入（需要重写正文里的附件相对链接）。");

    /// <inheritdoc />
    /// <remarks>
    /// 一张<strong>没有布局记录</strong>的便签被删掉时，这里什么都不额外做——
    /// 布局条目不存在，就没有「删了还漏一条垃圾在 layout.json 里」的问题。
    /// </remarks>
    public async Task DeleteNoteAsync(Guid noteId)
    {
        await _trash.MoveNoteToTrashAsync(noteId);

        // 内存里已经没有这张便签了，它那两个版本号留着只会占地方。
        // 万一它以后又从回收站回来，从 0 / 0 重新起算才是对的：回来的是另一个文件，
        // 与刚才那份「没落盘的改动」没有关系。
        Forget(noteId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 找不到条目时抛异常而不是静默返回：调用方拿着的 id 来自它自己那份列表，
    /// 对不上说明那份列表已经过期（比如回收站刚被清空），
    /// 静默成功会让界面以为「恢复好了」而列表里什么都不出现。
    /// </remarks>
    public async Task RestoreFromTrashAsync(Guid noteId, string? targetPath)
    {
        TrashEntry? entry = await _trash.FindByNoteIdAsync(noteId);

        if (entry is null)
        {
            throw new InvalidOperationException($"回收站里没有便签 {noteId} 对应的条目。");
        }

        await _trash.RestoreAsync(entry, targetPath);
    }

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
