using System.Globalization;
using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Trash;

/// <summary>
/// <see cref="ITrashStore"/> 的实现：笔记目录下的 <c>.lumimemo/trash/</c> 与 <c>trash-index.json</c>（§7）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>索引是可重建的派生数据</strong>（§7.2）。本类的每一次公开调用都先跑一遍
/// <see cref="RepairAsync"/>：把 <c>trash/</c> 目录的实际内容与索引对账，该补的补、该删的删、
/// 该标「已不在」的标上。<c>trash-index.json</c> 因此从来不是真相的来源——
/// 用户手动把文件拖回去、或者手动删掉一个文件，本类都能自己走回来。
/// 也正因如此，索引损坏时<strong>不备份改名</strong>：目录里有全部信息，
/// 留一个 <c>.corrupt-*</c> 只是往用户的笔记目录里塞垃圾。
/// </para>
/// <para>
/// <strong>不是线程安全的</strong>，靠调用方串行化。实际的调用方是
/// <c>LumiMemo.Core.Services.TrashService</c>，它的每个 <c>await</c> 都刻意回到 UI 线程，
/// 于是所有调用天然排在一根线程上（§3.4 规则 T1、T5）。
/// </para>
/// </remarks>
public sealed class FileSystemTrashStore : ITrashStore
{
    /// <summary>本文件的当前版本（§8.4）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>回收站文件名的时刻前缀（§7.1）。</summary>
    /// <remarks>
    /// 用<strong>本地时间</strong>而不是 UTC：这个名字会显示给用户（回收站列表里那一行），
    /// 而用户在 <c>.lumimemo/trash/</c> 里用资源管理器看修改时间时看到的也是本地时间，
    /// 两者只差一个前缀字符的位置却能让人对不上账。
    /// </remarks>
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary><see cref="TimestampFormat"/> 的长度，也是名字里第一个分隔符的下标。</summary>
    private const int TimestampLength = 15;

    /// <summary>探测便签 id 时最多读多少字节。</summary>
    /// <remarks>
    /// 回收站里可能有用户手动扔进来的大文件（视频、镜像）。为了一个 <c>id</c> 把 2GB 读进内存
    /// 显然不行，而 Front Matter 只可能出现在文件头部。超过这个大小就放弃探测——
    /// <c>noteId</c> 为空的后果仅仅是「按 id 恢复」这条路径找不到它，文件本身一切正常。
    /// </remarks>
    private const long MaxIdProbeBytes = 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly ILogger<FileSystemTrashStore> _logger;

    private readonly List<TrashEntry> _entries = [];

    private bool _loaded;

    public FileSystemTrashStore(
        IAppPaths paths,
        IClock clock,
        AtomicFileWriter writer,
        ILogger<FileSystemTrashStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _clock = clock;
        _writer = writer;
        _logger = logger;
    }

    // ---- 查询 ----

    /// <inheritdoc />
    /// <remarks>
    /// 按删除时间<strong>倒序</strong>返回：回收站列表是「我刚删的东西在哪」，
    /// 最近删的在最上面。索引文件本身按时间正序存（那是目录枚举的自然顺序）。
    /// </remarks>
    public async Task<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct).ConfigureAwait(false);

        return [.. _entries.OrderByDescending(entry => entry.DeletedAt)];
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>不需要索引</strong>：只查一下原路径在不在，一次 <c>File.Exists</c> 的事。
    /// 也<strong>不抛异常</strong>——它在 §7.3 对话框弹出之前被调用，那时炸掉等于
    /// 用户点一次「恢复」就崩一次。索引里的原路径被手改成逃出笔记目录时一律返回
    /// <see langword="false"/>，让后续的 <see cref="RestoreAsync"/> 去抛那个越界错误。
    /// </remarks>
    public bool IsOriginalPathOccupied(TrashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            string target = ResolveInsideNotesRoot(entry.OriginalRelativePath);

            return File.Exists(LongPath.Ensure(target)) || Directory.Exists(LongPath.Ensure(target));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---- 送进去 ----

    /// <inheritdoc />
    /// <remarks>
    /// 用 <c>File.Move</c> 而不是「复制 + 删除」：回收站与笔记同卷（§8.1 把
    /// <c>trash/</c> 放在笔记目录内就是为了这个），同卷的 <c>File.Move</c> 是原子换名，
    /// 中途断电也不会出现「源没了、目标也没有」的半截状态。
    /// </remarks>
    public async Task<TrashEntry> MoveFileToTrashAsync(
        string relativePath,
        Guid? noteId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        await PrepareAsync(ct).ConfigureAwait(false);

        string source = ResolveInsideNotesRoot(relativePath);

        if (!File.Exists(LongPath.Ensure(source)))
        {
            throw new FileNotFoundException($"要移入回收站的便签不存在：{source}", source);
        }

        string trashName = AllocateTrashName(Path.GetFileName(relativePath), isDirectory: false);

        Directory.CreateDirectory(LongPath.Ensure(TrashDirectory()));
        File.Move(LongPath.Ensure(source), LongPath.Ensure(Path.Combine(TrashDirectory(), trashName)));

        var entry = new TrashEntry
        {
            TrashName = trashName,
            OriginalRelativePath = relativePath,
            NoteId = noteId,
            DeletedAt = _clock.Now,
            Kind = TrashEntryKind.File,
            Size = new FileInfo(LongPath.Ensure(Path.Combine(TrashDirectory(), trashName))).Length,
        };

        _entries.Add(entry);
        await SaveIndexAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("便签已移入回收站：{Original} → {TrashName}。", relativePath, trashName);

        return entry;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 整体 <c>Directory.Move</c>，不是逐个移文件——§5.7 的「整个文件夹移到回收站」
    /// 要的是「一件东西」，恢复时也得一次搬回去，否则目录结构会在往返途中被压平。
    /// </remarks>
    public async Task<TrashEntry> MoveDirectoryToTrashAsync(
        string relativeDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativeDirectory);

        await PrepareAsync(ct).ConfigureAwait(false);

        string source = ResolveInsideNotesRoot(relativeDirectory);

        if (!Directory.Exists(LongPath.Ensure(source)))
        {
            throw new DirectoryNotFoundException($"要移入回收站的目录不存在：{source}");
        }

        string trashName = AllocateTrashName(Path.GetFileName(relativeDirectory), isDirectory: true);

        Directory.CreateDirectory(LongPath.Ensure(TrashDirectory()));
        Directory.Move(LongPath.Ensure(source), LongPath.Ensure(Path.Combine(TrashDirectory(), trashName)));

        var entry = new TrashEntry
        {
            TrashName = trashName,
            OriginalRelativePath = relativeDirectory,

            // 目录级条目没有便签 id（§7.2）：它是「一件东西」，不是「一张便签」。
            NoteId = null,
            DeletedAt = _clock.Now,
            Kind = TrashEntryKind.Directory,
            Size = DirectorySize(Path.Combine(TrashDirectory(), trashName)),
        };

        _entries.Add(entry);
        await SaveIndexAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("目录已移入回收站：{Original} → {TrashName}。", relativeDirectory, trashName);

        return entry;
    }

    // ---- 捞回来 ----

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 目标被占用时<strong>让路而不是覆盖</strong>（§7.3）：加 <c> (1)</c>、<c> (2)</c>……
    /// 直到空位。覆盖是不可逆的，而用户的意图从来不是「毁掉现在占着这个位置的那张便签」。
    /// </para>
    /// <para>
    /// 越界校验（§19.4）放在这里，而不是 Core 的 <c>TrashService</c>：
    /// 具体要落在文件系统的哪里，只有本层知道；<c>TrashService</c> 能做的只是
    /// 在它自己那条「恢复到根目录」的路径上取一个天然安全的文件名。
    /// </para>
    /// </remarks>
    public async Task<string> RestoreAsync(
        TrashEntry entry,
        string? targetRelativePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await PrepareAsync(ct).ConfigureAwait(false);

        string relativePath = string.IsNullOrWhiteSpace(targetRelativePath)
            ? entry.OriginalRelativePath
            : targetRelativePath;

        string requested = ResolveInsideNotesRoot(relativePath);
        string source = Path.Combine(TrashDirectory(), entry.TrashName);

        if (!File.Exists(LongPath.Ensure(source)) && !Directory.Exists(LongPath.Ensure(source)))
        {
            throw new FileNotFoundException(
                $"回收站里找不到「{entry.TrashName}」（可能在别处被删掉了，§7.2）。",
                source);
        }

        string target = AllocateFreePath(requested, entry.Kind);

        string? parent = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(LongPath.Ensure(parent));
        }

        if (entry.Kind == TrashEntryKind.Directory)
        {
            Directory.Move(LongPath.Ensure(source), LongPath.Ensure(target));
        }
        else
        {
            File.Move(LongPath.Ensure(source), LongPath.Ensure(target));
        }

        _entries.RemoveAll(candidate =>
            string.Equals(candidate.TrashName, entry.TrashName, StringComparison.OrdinalIgnoreCase));

        await SaveIndexAsync(ct).ConfigureAwait(false);

        string restored = RelativeToNotesRoot(target);

        _logger.LogInformation(
            "已从回收站恢复：{TrashName} → {Restored}（请求为 {Requested}）。",
            entry.TrashName,
            restored,
            relativePath);

        return restored;
    }

    // ---- 清理 ----

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 删不掉的条目<strong>留在索引里</strong>，而不是先从索引里划掉再说。
    /// 反过来做的话，那个文件会立刻变成「目录里有、索引里没有」，
    /// 下次一致性检查又把它当成新条目补回来——用户会看到自己刚清空的回收站里冒出一条。
    /// </para>
    /// <para>
    /// <strong>不可逆</strong>：本层不问任何问题，两次确认是界面的事（§7.4）。
    /// </para>
    /// </remarks>
    public async Task EmptyAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct).ConfigureAwait(false);

        int deleted = 0;
        int failed = 0;

        foreach (TrashEntry entry in _entries.ToList())
        {
            if (TryDeleteEntryFile(entry))
            {
                _entries.Remove(entry);
                deleted++;
            }
            else
            {
                failed++;
            }
        }

        await SaveIndexAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("回收站已清空：删除 {Deleted} 项，{Failed} 项被占用未能删除。", deleted, failed);
    }

    /// <inheritdoc />
    /// <param name="retentionDays">保留天数，必须 &gt;= 1。</param>
    /// <remarks>
    /// <strong>「0 表示永不清理」的判断不在这里</strong>，而在 <c>TrashService.PurgeExpiredAsync</c>。
    /// 本层只认「多少天」这个数，因为「永不清理」是<strong>策略</strong>，
    /// 而策略来自用户设置；把策略翻译成「不调用本方法」比翻译成「传一个魔法值」要诚实得多。
    /// 为了堵住那条歧义路径，这里对 <c>&lt;= 0</c> 直接抛异常——
    /// 传 0 进来的人想要的显然是「全部删掉」，而那不该由一次笔误实现。
    /// </remarks>
    public async Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        await PrepareAsync(ct).ConfigureAwait(false);

        DateTimeOffset cutoff = _clock.Now.AddDays(-retentionDays);
        int purged = 0;

        foreach (TrashEntry entry in _entries.ToList())
        {
            if (entry.DeletedAt > cutoff)
            {
                continue;
            }

            if (TryDeleteEntryFile(entry))
            {
                _entries.Remove(entry);
                purged++;
            }
        }

        if (purged > 0)
        {
            await SaveIndexAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "按保留期（{Days} 天）清理回收站：删除 {Purged} 项。",
                retentionDays,
                purged);
        }

        return purged;
    }

    // ---- 索引读写 ----

    /// <summary>读一次索引（只读一次），并保证 <c>trash/</c> 目录可用。</summary>
    /// <remarks>
    /// 刻意<strong>不建 <c>trash/</c> 目录</strong>：一次「启动 → 打开回收站 → 关掉」
    /// 不该让用户的笔记目录里多出一个空文件夹。<see cref="MoveFileToTrashAsync"/>
    /// 之类真的要往里放东西的路径自己负责创建。
    /// </remarks>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _entries.Clear();

        string path = _paths.TrashIndexFile;
        string? json = await TryReadIndexAsync(path, ct).ConfigureAwait(false);

        if (json is null)
        {
            return;
        }

        TrashIndexFileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<TrashIndexFileModel>(json, JsonFileFormat.Options);
        }
        catch (JsonException ex)
        {
            // §7.2 第 3 行：索引损坏就完全凭目录内容重建。
            // 不备份改名——目录里有全部信息，重建出来的索引与损坏前等价。
            _logger.LogWarning(
                "trash-index.json 无法解析（{ExceptionType}），将按 trash 目录内容重建。",
                ex.GetType().Name);
            return;
        }

        if (model?.Entries is null || model.Version > CurrentVersion)
        {
            if (model is not null && model.Version > CurrentVersion)
            {
                _logger.LogWarning(
                    "trash-index.json 来自更新的版本（{Version} > {Current}），按 trash 目录内容重建。",
                    model.Version,
                    CurrentVersion);
            }

            return;
        }

        foreach (TrashEntryModel item in model.Entries)
        {
            if (string.IsNullOrWhiteSpace(item.TrashName))
            {
                continue;
            }

            _entries.Add(item.ToEntry());
        }
    }

    /// <summary>读索引 + 按 §7.2 的表格对账。</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        await RepairAsync(ct).ConfigureAwait(false);
    }

    /// <summary>把索引与 <c>trash/</c> 目录的实际内容对平（§7.2）。</summary>
    /// <remarks>
    /// 三种不一致各有一条规则：
    /// <list type="number">
    ///   <item>目录里有、索引里没有 → 从文件名解析出删除时刻与原文件名，补一条；文件级条目再去文件头部读一次便签 <c>id</c>。</item>
    ///   <item>索引里有、目录里没有 → 原路径已被占用说明用户自己把文件拖回去了，静默移除条目；否则保留并标 <see cref="TrashEntry.IsFileMissing"/>。</item>
    ///   <item>索引不存在或损坏 → 见 <see cref="EnsureLoadedAsync"/>，完全凭目录内容重建。</item>
    /// </list>
    /// <para>
    /// 只有<strong>磁盘上真的对不平</strong>时才写盘：一次纯粹的「打开回收站看看」不该改
    /// <c>trash-index.json</c> 的修改时间。第 2 种情况里标上的
    /// <see cref="TrashEntry.IsFileMissing"/> 不算「对不平」——它不落盘，
    /// 为它重写一次内容相同的文件没有意义。
    /// </para>
    /// </remarks>
    private async Task RepairAsync(CancellationToken ct)
    {
        Dictionary<string, TrashEntry> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrashEntry entry in _entries)
        {
            byName[entry.TrashName] = entry;
        }

        var result = new List<TrashEntry>(_entries.Count);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 两个标志分开是刻意的。structural 是「磁盘上真有事」，要重写索引；
        // runtime 只是内存里的派生标志翻了个面（IsFileMissing），而它根本不落盘——
        // 为它重写一次内容完全相同的文件，只会平白改掉索引的修改时间。
        bool structural = false;
        bool runtime = false;

        string trashDirectory = TrashDirectory();

        if (Directory.Exists(LongPath.Ensure(trashDirectory)))
        {
            foreach (string file in Directory.GetFiles(LongPath.Ensure(trashDirectory)))
            {
                string name = Path.GetFileName(file);
                present.Add(name);

                if (byName.TryGetValue(name, out TrashEntry? existing) && existing.Kind == TrashEntryKind.File)
                {
                    long size = new FileInfo(file).Length;
                    structural |= existing.Size != size;
                    existing.Size = size;
                    runtime |= existing.IsFileMissing;
                    existing.IsFileMissing = false;
                    result.Add(existing);
                }
                else
                {
                    result.Add(await RebuildEntryAsync(name, file, TrashEntryKind.File, ct).ConfigureAwait(false));
                    structural = true;
                }
            }

            foreach (string directory in Directory.GetDirectories(LongPath.Ensure(trashDirectory)))
            {
                string name = Path.GetFileName(directory);
                present.Add(name);

                if (byName.TryGetValue(name, out TrashEntry? existing) && existing.Kind == TrashEntryKind.Directory)
                {
                    long size = DirectorySize(directory);
                    structural |= existing.Size != size;
                    existing.Size = size;
                    runtime |= existing.IsFileMissing;
                    existing.IsFileMissing = false;
                    result.Add(existing);
                }
                else
                {
                    result.Add(await RebuildEntryAsync(name, directory, TrashEntryKind.Directory, ct).ConfigureAwait(false));
                    structural = true;
                }
            }
        }

        string notesFolder = NotesFolder();

        foreach (TrashEntry entry in _entries)
        {
            if (present.Contains(entry.TrashName))
            {
                continue;
            }

            // 原路径已经被占着 → 用户自己把文件从 trash 里拖回去了。条目静默移除：
            // 继续留着它，用户会在回收站里看到一张此刻正开着的便签。
            if (File.Exists(LongPath.Ensure(Path.Combine(notesFolder, entry.OriginalRelativePath)))
                || Directory.Exists(LongPath.Ensure(Path.Combine(notesFolder, entry.OriginalRelativePath))))
            {
                _logger.LogInformation(
                    "回收站条目 {TrashName} 对应的文件已回到原位置，索引条目移除。",
                    entry.TrashName);
                structural = true;
                continue;
            }

            runtime |= !entry.IsFileMissing;
            entry.IsFileMissing = true;
            result.Add(entry);
        }

        if (!structural && !runtime)
        {
            return;
        }

        _entries.Clear();
        _entries.AddRange(result);

        if (structural)
        {
            await SaveIndexAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>凭目录里那个文件（或目录）重建一条索引记录（§7.2 第 1 行）。</summary>
    private async Task<TrashEntry> RebuildEntryAsync(
        string trashName,
        string fullPath,
        TrashEntryKind kind,
        CancellationToken ct)
    {
        (DateTimeOffset? deletedAt, string originalName) = ParseTrashName(trashName);

        // 解析不出时刻说明这个文件是用户手动扔进来的（或者名字被改过）。
        // 用文件自己的修改时间兜底：保留期清理需要一个时间，而「不知道」不是个可用的时间。
        deletedAt ??= new DateTimeOffset(File.GetLastWriteTimeUtc(LongPath.Ensure(fullPath)), TimeSpan.Zero);

        TrashEntry entry = new()
        {
            TrashName = trashName,
            OriginalRelativePath = originalName,
            NoteId = kind == TrashEntryKind.File ? await TryReadNoteIdAsync(fullPath, ct).ConfigureAwait(false) : null,
            DeletedAt = deletedAt.Value,
            Kind = kind,
            Size = kind == TrashEntryKind.File ? new FileInfo(fullPath).Length : DirectorySize(fullPath),
        };

        _logger.LogInformation(
            "回收站索引缺少 {TrashName}，已从目录内容补建（原路径按 {Original} 推定）。",
            trashName,
            entry.OriginalRelativePath);

        return entry;
    }

    /// <summary>从回收站文件名里解析出删除时刻与原文件名（§7.1 的 <c>{时刻}-{原名}</c>）。</summary>
    /// <remarks>
    /// 解析失败时把整个名字当成原文件名：至少「恢复到根目录」那一档还能用，
    /// 而且比丢掉这条记录要好。
    /// </remarks>
    private static (DateTimeOffset? DeletedAt, string OriginalName) ParseTrashName(string trashName)
    {
        if (trashName.Length > TimestampLength + 1 && trashName[TimestampLength] == '-'
            && DateTimeOffset.TryParseExact(
                trashName.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTimeOffset stamp))
        {
            return (stamp, trashName[(TimestampLength + 1)..]);
        }

        return (null, trashName);
    }

    /// <summary>读文件头部的 Front Matter 拿便签 id；读不出来返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 与仓储层的解析共用 <see cref="FrontMatterParser"/>，因此这里认的 Front Matter 语法
    /// 与正常扫描完全一致——用户改过的、带 BOM 的、行尾是 CRLF 的都照样认。
    /// 解析器对任何字节都不抛异常，所以这里只需要接住文件系统的失败。
    /// </remarks>
    private async Task<Guid?> TryReadNoteIdAsync(string path, CancellationToken ct)
    {
        try
        {
            string ensured = LongPath.Ensure(path);
            var info = new FileInfo(ensured);

            if (info.Length > MaxIdProbeBytes)
            {
                _logger.LogWarning(
                    "回收站里的 {FileName} 有 {Size} 字节，跳过 id 探测，它将无法按便签恢复。",
                    info.Name,
                    info.Length);
                return null;
            }

            byte[] raw = await File.ReadAllBytesAsync(ensured, ct).ConfigureAwait(false);

            return FrontMatterParser.Parse(raw).Result.Id;
        }
        catch (IOException ex)
        {
            _logger.LogWarning("读取回收站文件的 id 失败（{ExceptionType}）：{Path}。", ex.GetType().Name, path);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("读取回收站文件的 id 失败（{ExceptionType}）：{Path}。", ex.GetType().Name, path);
            return null;
        }
    }

    /// <summary>把内存里的索引写回磁盘。</summary>
    /// <remarks>
    /// 写失败只记 Warning 不抛：索引是派生数据，丢了下次一致性检查会从目录内容重建，
    /// 用户的便签文件一个字节都不会少。为它中断一次回收站操作（尤其是「移入」——
    /// 那时文件已经搬完了）是得不偿失的。
    /// </remarks>
    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var model = new TrashIndexFileModel
        {
            Version = CurrentVersion,
            Entries = [.. _entries.Select(TrashEntryModel.From)],
        };

        byte[] bytes = JsonFileFormat.WithTrailingNewline(
            JsonSerializer.SerializeToUtf8Bytes(model, JsonFileFormat.Options));

        try
        {
            await _writer.WriteAsync(_paths.TrashIndexFile, bytes, _clock.Now, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "写入 trash-index.json 失败（{ExceptionType}），下次会按 trash 目录内容重建。",
                ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "写入 trash-index.json 失败（{ExceptionType}），下次会按 trash 目录内容重建。",
                ex.GetType().Name);
        }
    }

    /// <summary>读 <c>trash-index.json</c>；不存在或读不出来返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 读失败时<strong>不</strong>当成损坏：文件被同步工具或杀毒软件独占几百毫秒是常事，
    /// 重试一轮仍读不出来，就当这一轮没有索引，原文件原地不动，交给接下来的对账去补。
    /// </remarks>
    private async Task<string?> TryReadIndexAsync(string path, CancellationToken ct)
    {
        string ensured = LongPath.Ensure(path);

        if (!File.Exists(ensured))
        {
            return null;
        }

        try
        {
            return await StorageRetry
                .RunAsync(() => File.ReadAllText(ensured), StorageRetry.DefaultDelaysMilliseconds, ct)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "读取 trash-index.json 失败，本次按 trash 目录内容重建（{ExceptionType}）。",
                ex.GetType().Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "读取 trash-index.json 失败，本次按 trash 目录内容重建（{ExceptionType}）。",
                ex.GetType().Name);
            return null;
        }
    }

    // ---- 路径与命名 ----

    /// <summary>给一个新条目分配在 <c>trash/</c> 下的名字（§7.1）。</summary>
    /// <remarks>
    /// <para>
    /// 同一秒内删掉两个同名文件会撞车（时间戳只精确到秒），所以撞了就退化成
    /// <c>{时刻}-{主名}-{n}{扩展名}</c>。
    /// </para>
    /// <para>
    /// 除了文件系统，还要查一遍索引：索引里那条的<strong>文件可能已经不在了</strong>（标着
    /// <see cref="TrashEntry.IsFileMissing"/>）。名字一旦被复用，那条坏记录会忽然「找到」文件，
    /// 用户会在回收站里看到两张一模一样的便签，恢复哪一张都是它。
    /// </para>
    /// </remarks>
    private string AllocateTrashName(string originalFileName, bool isDirectory)
    {
        string stamp = _clock.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string candidate = $"{stamp}-{originalFileName}";

        if (!TrashNameTaken(candidate))
        {
            return candidate;
        }

        string stem = isDirectory ? originalFileName : Path.GetFileNameWithoutExtension(originalFileName);
        string extension = isDirectory ? string.Empty : Path.GetExtension(originalFileName);

        for (int index = 1; ; index++)
        {
            candidate = $"{stamp}-{stem}-{index}{extension}";

            if (!TrashNameTaken(candidate))
            {
                return candidate;
            }
        }
    }

    private bool TrashNameTaken(string trashName)
    {
        string path = Path.Combine(TrashDirectory(), trashName);

        if (File.Exists(LongPath.Ensure(path)) || Directory.Exists(LongPath.Ensure(path)))
        {
            return true;
        }

        return _entries.Any(entry => string.Equals(entry.TrashName, trashName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>目标已被占用时往后找第一个空位（§7.3）。</summary>
    /// <remarks>
    /// 文件按 <c>周报-20260920-4c88f012 (1).md</c> 的样子加序号——序号在扩展名<strong>之前</strong>，
    /// 否则它就变成一个没有 <c>.md</c> 后缀的文件，本程序（以及一堆别的工具）都认不出它是便签。
    /// 目录没有扩展名这回事，直接 <c>名字 (1)</c>。
    /// </remarks>
    private static string AllocateFreePath(string requested, TrashEntryKind kind)
    {
        if (!Exists(requested))
        {
            return requested;
        }

        string directory = Path.GetDirectoryName(requested) ?? string.Empty;
        string name = Path.GetFileName(requested);
        string stem = kind == TrashEntryKind.Directory ? name : Path.GetFileNameWithoutExtension(name);
        string extension = kind == TrashEntryKind.Directory ? string.Empty : Path.GetExtension(name);

        for (int index = 1; ; index++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({index}){extension}");

            if (!Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool Exists(string path) =>
        File.Exists(LongPath.Ensure(path)) || Directory.Exists(LongPath.Ensure(path));

    /// <summary>把相对路径解析成笔记目录内的完整路径，越界就抛（§19.4）。</summary>
    /// <remarks>
    /// 必须在<strong>规范化之后</strong>比较前缀（<see cref="NoteFileNameBuilder.IsInsideNotesRoot"/>
    /// 内部做的就是这件事），且 <c>\\?\</c> 前缀要等校验过了再加——
    /// 前缀会关掉 Win32 自己的规范化，带进去的 <c>..</c> 就再也不会被消解。
    /// </remarks>
    private string ResolveInsideNotesRoot(string relativePath)
    {
        string root = NotesFolder();

        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException($"回收站只接受相对笔记目录的路径：{relativePath}");
        }

        string full = Path.GetFullPath(relativePath, root);

        if (!NoteFileNameBuilder.IsInsideNotesRoot(full, root))
        {
            throw new InvalidOperationException($"路径跑出了笔记目录，拒绝执行：{relativePath}");
        }

        return full;
    }

    private string RelativeToNotesRoot(string fullPath) =>
        Path.GetRelativePath(NotesFolder(), fullPath);

    private string TrashDirectory() => _paths.TrashDirectory;

    private string NotesFolder() =>
        _paths.NotesFolder ?? throw new InvalidOperationException(
            "尚未选定笔记目录，回收站不可用（启动序列见 §17.1）。");

    /// <summary>删除条目对应的文件或目录。删不掉返回 <see langword="false"/> 并已记日志。</summary>
    private bool TryDeleteEntryFile(TrashEntry entry)
    {
        string path = Path.Combine(TrashDirectory(), entry.TrashName);

        try
        {
            if (entry.Kind == TrashEntryKind.Directory)
            {
                if (Directory.Exists(LongPath.Ensure(path)))
                {
                    Directory.Delete(LongPath.Ensure(path), recursive: true);
                }
            }
            else if (File.Exists(LongPath.Ensure(path)))
            {
                File.Delete(LongPath.Ensure(path));
            }

            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "删除回收站条目 {TrashName} 失败，它会被留到下次清理（{ExceptionType}）。",
                entry.TrashName,
                ex.GetType().Name);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "删除回收站条目 {TrashName} 失败，它会被留到下次清理（{ExceptionType}）。",
                entry.TrashName,
                ex.GetType().Name);
            return false;
        }
    }

    /// <summary>一个目录里所有文件的总字节数，仅用于展示。</summary>
    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(LongPath.Ensure(path)))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        long total = 0;

        foreach (string file in Directory.EnumerateFiles(LongPath.Ensure(path), "*", options))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // 算不出来就当 0：大小只是个展示用的数字，不值得为它中断一次目录统计。
            }
        }

        return total;
    }
}
