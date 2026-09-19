using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Stores;

namespace LumiMemo.Core.Services;

/// <summary>
/// 回收站的业务入口（§7）：把便签送进去、捞回来、按保留期清理。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它才是碰 <see cref="ITrashStore"/> 的那一层。</strong> <c>INoteService</c> 的文档里
/// 写死了「不直接移动回收站文件」，而删除与恢复又必须同时改内存与磁盘——
/// 于是这两件事都落在本类上，<c>INoteService</c> 相应地把
/// <c>DeleteNoteAsync</c> / <c>RestoreFromTrashAsync</c> 两个方法让了出来。
/// 让便签服务去转手一个它本来就不该知道的东西（回收站目录、索引、保留期），
/// 只会让「谁负责这一块」重新变得含混。
/// </para>
/// <para>
/// <strong>内存的增删在本类里完成</strong>（<see cref="NoteStore"/> 与 <see cref="SearchIndex"/>），
/// 与 <c>NoteService.LoadAllAsync</c> 同一条线程约定：本类每个 <c>await</c>
/// <strong>刻意不加 <c>ConfigureAwait(false)</c></strong>，续体回到 UI 线程再改内存。
/// 加一个就会让恢复操作在后台线程上写被界面绑定的状态（§3.4 规则 T1、T5）。
/// </para>
/// <para>
/// <strong>恢复走的不是整目录重扫。</strong> 重扫会把 <see cref="NoteStore"/> 里的
/// <see cref="Note"/> 实例全部换成新对象，而已经打开的便签窗口手里还攥着旧的那个——
/// 用户接着编辑就写进了一个脱离 Store 的对象，改完什么都不会发生（§18.4）。
/// 因此这里只把恢复回来的那几个文件读进内存。
/// </para>
/// </remarks>
public sealed class TrashService
{
    /// <summary>目录级条目恢复时，往哪些文件里找便签（§5.7 的递归扫描规则）。</summary>
    private const string NoteExtension = ".md";

    /// <summary>原子写盘留下的临时文件后缀，连同 <c>AtomicFileWriter</c> 的取值。</summary>
    /// <remarks>
    /// 刻意写死而不是引用 <c>AtomicFileWriter.TempSuffix</c>：那个类在 Infrastructure 层，
    /// 而 Core 不允许引用它（§4.1）。两处必须一起改——这是这条分层规则的真实代价，
    /// 记在这里比将来让人对着一个莫名跳过的文件猜要好。
    /// </remarks>
    private const string TempSuffix = ".lumitmp";

    private readonly ITrashStore _trash;
    private readonly IAppPaths _paths;
    private readonly NoteStore _notes;
    private readonly SearchIndex _index;
    private readonly INoteRepository _repository;

    public TrashService(
        ITrashStore trash,
        IAppPaths paths,
        NoteStore notes,
        SearchIndex index,
        INoteRepository repository)
    {
        ArgumentNullException.ThrowIfNull(trash);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(repository);

        _trash = trash;
        _paths = paths;
        _notes = notes;
        _index = index;
        _repository = repository;
    }

    /// <summary>
    /// 回收站保留多少天。由启动序列从 <c>AppSettings.TrashRetentionDays</c> 灌进来（§7.4）。
    /// </summary>
    /// <remarks>
    /// <strong><c>0</c> 表示永不清理</strong>，与 §7.4 的「可选 7 / 30 / 90 / 0」一致。
    /// 做成可写属性而不是构造参数，与 <c>AutoSaveService.DelayMilliseconds</c> 同一手法：
    /// 设置是启动时才知道的，不该成为构造函数的一部分。
    /// </remarks>
    public int RetentionDays { get; set; } = 30;

    /// <summary>列出回收站里的条目，返回前已完成一次 §7.2 的一致性修复。</summary>
    public Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default) =>
        _trash.ListAsync(ct);

    /// <summary>
    /// 条目原本的位置是否已被占用。为真时上层应先弹 §7.3 的三选一对话框。
    /// </summary>
    public bool NeedsConflictResolution(TrashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return _trash.IsOriginalPathOccupied(entry);
    }

    /// <summary>
    /// §7.3 里「恢复到笔记目录根」那一档的目标相对路径。
    /// </summary>
    /// <remarks>
    /// 只取最后一段文件名，所以结果<strong>必然</strong>落在笔记目录内——
    /// 被恢复的路径里就算带着 <c>..</c>（索引被手改过），也不可能借此逃出去（§19.4）。
    /// 目录级条目同样适用：拿到的是那个目录名。
    /// </remarks>
    public static string RootTargetFor(TrashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Path.GetFileName(entry.OriginalRelativePath);
    }

    // ---- 送进去 ----

    /// <summary>
    /// 把一张便签移入回收站，并从内存里摘掉它（§7.1）。
    /// </summary>
    /// <remarks>
    /// <strong>只移动文件，不删文件</strong>——这是回收站存在的全部意义。
    /// 内存侧摘掉是必须的：不摘的话管理器的列表里还留着一条，
    /// 用户点开它就会看到自己刚删掉的便签。
    /// <para>
    /// <strong>layout 不动。</strong> §7.3 明说了按 id 索引的窗口位置在删除时不清除，
    /// 于是恢复之后折叠状态、置顶、位置全都自己回来。删的时候顺手清掉的话，
    /// 用户会以为「回收站只还回了内容」。
    /// </para>
    /// </remarks>
    public async Task<TrashEntry> MoveNoteToTrashAsync(Guid noteId, CancellationToken ct = default)
    {
        Note? note = _notes.TryGet(noteId)
            ?? throw new InvalidOperationException(
                $"便签 {noteId} 不在内存里，无法移入回收站。");

        TrashEntry entry = await _trash
            .MoveFileToTrashAsync(RelativeToNotesFolder(note.FilePath), noteId, ct);

        _notes.Remove(noteId);
        _index.OnNoteRemoved(noteId);

        return entry;
    }

    /// <summary>
    /// 把整个目录移入回收站（§5.7 的「整个文件夹移到回收站」）。
    /// </summary>
    /// <remarks>
    /// 本方法<strong>不碰内存</strong>：目录里的便签此刻若还留在列表上，
    /// 用户点开就会撞上「文件不存在」，而那是下一个阶段（文件夹视图与删除）才有的入口，
    /// 那时由调用方决定要不要重扫。现在就替它做决定只会写出一段没人调用的代码。
    /// </remarks>
    public Task<TrashEntry> MoveDirectoryToTrashAsync(string relativeDirectory, CancellationToken ct = default) =>
        _trash.MoveDirectoryToTrashAsync(relativeDirectory, ct);

    // ---- 捞回来 ----

    /// <summary>
    /// 把条目恢复到笔记目录，并把恢复回来的便签读进内存。
    /// </summary>
    /// <param name="entry">要恢复的条目。</param>
    /// <param name="targetRelativePath">
    /// 恢复到哪。传 <see langword="null"/> 表示回原位（§7.3 的第一档）；
    /// 传 <see cref="RootTargetFor"/> 的结果表示放到笔记目录根（第二档）。
    /// </param>
    /// <param name="ct">取消标记。</param>
    /// <returns>实际恢复到的相对路径。</returns>
    public async Task<string> RestoreAsync(
        TrashEntry entry,
        string? targetRelativePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string relativePath = await _trash.RestoreAsync(entry, targetRelativePath, ct);

        await ImportRestoredAsync(entry, relativePath, ct);

        return relativePath;
    }

    /// <summary>按 id 找回收站里的那张便签。找不到返回 <see langword="null"/>。</summary>
    public async Task<TrashEntry?> FindByNoteIdAsync(Guid noteId, CancellationToken ct = default)
    {
        foreach (TrashEntry entry in await _trash.ListAsync(ct))
        {
            if (entry.NoteId == noteId)
            {
                return entry;
            }
        }

        return null;
    }

    // ---- 清理 ----

    /// <summary>清空回收站（§7.4）。<strong>不可逆</strong>，调用方必须先问过用户两次。</summary>
    public Task EmptyAsync(CancellationToken ct = default) => _trash.EmptyAsync(ct);

    /// <summary>
    /// 按保留期清理（§7.4）。<see cref="RetentionDays"/> 为 <c>0</c> 时什么都不做。
    /// </summary>
    /// <returns>被清理的条目数，供托盘提示「已清理 N 个项目」。</returns>
    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        RetentionDays <= 0 ? Task.FromResult(0) : _trash.PurgeExpiredAsync(RetentionDays, ct);

    // ---- 内部 ----

    /// <summary>把恢复回来的文件读进内存。</summary>
    private async Task ImportRestoredAsync(TrashEntry entry, string relativePath, CancellationToken ct)
    {
        string target = Path.Combine(RequireNotesFolder(), relativePath);

        if (entry.Kind == TrashEntryKind.Directory)
        {
            // 目录级条目一次带回一整棵子树（§7.3）。逐个读，不走整目录重扫。
            foreach (string file in EnumerateNoteFiles(target))
            {
                await ImportFileAsync(file, ct);
            }

            return;
        }

        await ImportFileAsync(target, ct);
    }

    /// <summary>读一个文件并放进内存；id 与现有便签冲突时重新分配（§7.3）。</summary>
    /// <remarks>
    /// 读不出来时<strong>静默跳过而不是让恢复失败</strong>。这一刻文件已经在笔记目录里了，
    /// 而调用方要的正是这个。把「内存没同步上」报成「恢复失败」会让用户以为便签丢了，
    /// 于是再去点一次恢复——那时条目已经被移出索引，他只会得到「回收站里没有这条」，
    /// 反而更慌。漏掉的这一张由下一次启动扫描带回来。
    /// </remarks>
    private async Task ImportFileAsync(string path, CancellationToken ct)
    {
        Note? note;
        try
        {
            note = (await _repository.ReloadAsync(path, ct)).Note;
        }
        catch (NoteTemporarilyLockedException)
        {
            // 文件被别的进程独占（网盘在同步、杀毒软件在扫）。这是暂时的，
            // 重启一次就好了，不值得为此把一次成功的恢复报成失败。
            return;
        }

        if (note is null)
        {
            return;
        }

        if (_notes.Contains(note.Id))
        {
            // §7.3：用户删掉一张便签之后又新建了一张，恰好复制了同一个 id。
            // 恢复的这张让路——重新分配 id 并写回文件，否则 Store 里会被静默覆盖，
            // 用户在回收站里点一次「恢复」，丢掉的是另一张便签。
            note = WithNewId(note);
            await _repository.SaveAsync(note, ct);
        }

        _notes.Add(note);
        _index.OnNoteAdded(note);
    }

    /// <summary>复制一张便签并换掉它的 <c>Id</c>。</summary>
    /// <remarks>
    /// <see cref="Note.Id"/> 是 <c>init</c> 的，这正是它该有的样子——身份不变量由类型守住，
    /// 于是「换个 id」只能是一次完整重建，而不可能在某处随手改一个字段。
    /// 代价是这里要逐个搬字段；漏搬一个就会静默丢用户数据（颜色、标签、未知 Front Matter 键都在列）。
    /// </remarks>
    private static Note WithNewId(Note source) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = source.FilePath,
        Content = source.Content,
        Color = source.Color,
        Tags = [.. source.Tags],
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        LineEnding = source.LineEnding,
        HadBom = source.HadBom,
        FrontMatterTail = source.FrontMatterTail,
        UnknownFrontMatterKeys = [.. source.UnknownFrontMatterKeys],
        ParseIssues = [.. source.ParseIssues],
    };

    /// <summary>递归枚举一个目录下的便签文件。</summary>
    /// <remarks>
    /// 跳过以 <c>.</c> 开头的目录与 <c>.lumitmp</c> 残留，与仓储层的扫描规则一致。
    /// <strong>不跳过附件目录</strong>：附件目录只可能在笔记目录根上（§6.1），
    /// 而本方法的入口永远是一棵刚恢复回来的子树。
    /// </remarks>
    private static IEnumerable<string> EnumerateNoteFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                if (!Path.GetFileName(subdirectory).StartsWith('.'))
                {
                    pending.Push(subdirectory);
                }
            }

            foreach (string file in files)
            {
                if (file.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>把便签的绝对路径换成相对笔记目录的路径。</summary>
    private string RelativeToNotesFolder(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string relative = Path.GetRelativePath(RequireNotesFolder(), filePath);

        // 「..」开头说明这张便签其实在笔记目录外面（用户改过 settings.json，
        // 或者文件是被别处移进来的）。让它去回收站意味着把笔记目录外的文件搬进笔记目录，
        // 这种越界动作宁可当场失败。
        if (relative.StartsWith('.') || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException(
                $"便签不在笔记目录内，拒绝移入回收站：{filePath}");
        }

        return relative;
    }

    private string RequireNotesFolder() =>
        _paths.NotesFolder ?? throw new InvalidOperationException(
            "尚未选定笔记目录，回收站不可用（启动序列见 §17.1）。");
}
