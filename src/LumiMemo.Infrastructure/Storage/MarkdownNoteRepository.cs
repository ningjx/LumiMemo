using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// <see cref="INoteRepository"/> 的实现：一个 <c>.md</c> 文件就是一条便签，没有数据库（§5.1）。
/// </summary>
/// <remarks>
/// <para>
/// 本类是「解析器 + 序列化器 + 原子写入」三件纯工具的串联者，自己只负责三件需要
/// 文件系统知识的事：找到文件、给便签定身份（id）、以及决定什么时候必须回写。
/// </para>
/// <para>
/// <strong>启动扫描会写用户文件</strong>，这是有意为之，也是唯一允许在用户没做任何操作时
/// 动他文件的地方——只发生在「文件缺 id」和「Front Matter 读不懂」两种情况下（§5.5、§5.10）。
/// 其余文件一个字节都不碰。
/// </para>
/// <para>
/// 切换笔记目录（§8.6）后必须重新调用一次 <see cref="LoadAllAsync"/>：按路径缓存的
/// 编码形态与内容哈希都来自上一次扫描，重新扫描会把它们全部刷新。
/// </para>
/// </remarks>
public sealed class MarkdownNoteRepository : INoteRepository
{
    /// <summary>文件系统时间戳早于这一年的，视为「这个文件系统不提供该时间」，不予采信。</summary>
    private const int MinPlausibleTimestampYear = 1980;

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly ILogger<MarkdownNoteRepository> _logger;
    private readonly IReadOnlyList<int> _retryDelays;

    /// <summary>
    /// 按路径记住「这个文件在磁盘上长什么样」（§5.9）。
    /// </summary>
    /// <remarks>
    /// <see cref="NoteFileState.ContentHash"/> 是保存前自检的依据：拿它和「将要写出的字节」
    /// 比一比就知道这次保存是不是白写。它记的是<strong>读入时的原始字节</strong>的哈希，
    /// 而不是磁盘此刻的字节——理由见 <see cref="SaveAsync"/>。
    /// </remarks>
    private readonly ConcurrentDictionary<string, NoteFileState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public MarkdownNoteRepository(
        IAppPaths paths,
        IClock clock,
        AtomicFileWriter writer,
        ILogger<MarkdownNoteRepository> logger,
        IReadOnlyList<int>? lockRetryDelaysMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _clock = clock;
        _writer = writer;
        _logger = logger;
        _retryDelays = lockRetryDelaysMilliseconds ?? StorageRetry.DefaultDelaysMilliseconds;
    }

    /// <summary>Front Matter 里 <c>color</c> 缺失或非法时使用的颜色（§5.10）。</summary>
    /// <remarks>
    /// 做成可写属性而不是构造参数：它来自设置，而设置可能在程序运行期间被改。
    /// 若做成构造参数，用户改完默认色就得重建整个仓储。
    /// </remarks>
    public NoteColor DefaultColor { get; set; } = NoteColor.Yellow;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 顺序严格照 §5.5：清理残留临时文件 → 扫描 → 解析 → 补 id → 处理 id 冲突 → 建对象。
    /// <strong>不启动文件监听</strong>——那必须等这一整套动作做完，否则补写 id 引发的一批
    /// 文件变更事件会涌进监听器，与去抖、自写抑制逻辑纠缠出难以复现的时序问题。
    /// </para>
    /// <para>
    /// 单个文件读不出来<strong>不会</strong>让整次扫描失败。被独占锁定的文件跳过并记日志：
    /// 启动时手头没有「上次内容」可保留，比硬塞一张空便签进去更安全——空便签一旦被保存，
    /// 就把用户的文件覆盖成空的了。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        string? root = _paths.NotesFolder;
        if (string.IsNullOrWhiteSpace(root))
        {
            // 尚未选定笔记目录是合法的启动状态（§8.6 首次运行向导），不是错误。
            _logger.LogInformation("尚未选定笔记目录，本次不加载任何便签。");
            return [];
        }

        int cleaned = AtomicFileWriter.CleanupOrphanedTempFiles(root);
        if (cleaned > 0)
        {
            _logger.LogInformation("清理了 {Count} 个上次运行残留的临时文件。", cleaned);
        }

        var pending = new List<PendingNote>();

        foreach (string file in EnumerateNoteFiles(root))
        {
            ct.ThrowIfCancellationRequested();

            PendingNote? note = await TryReadPendingNoteAsync(file, ct).ConfigureAwait(false);
            if (note is not null)
            {
                pending.Add(note);
            }
        }

        ResolveIds(pending);
        await BackfillIdsAsync(pending, ct).ConfigureAwait(false);

        var notes = new List<Note>(pending.Count);
        foreach (PendingNote item in pending)
        {
            Note note = CreateNote(item);

            if (item.BackfillIssue is not null && !item.WrittenBack)
            {
                // 回写失败时这个标记<strong>仍然成立</strong>：磁盘上确实还没有 id。
                // 保留它，用户和管理器才看得见「这张便签还没同步」（§5.5）。
                note.ParseIssues.Add(item.BackfillIssue);
            }

            // id 到这一步才最终确定（可能因冲突被重新分配过），记下来供 §5.5 的
            // 「外部清空 Front Matter」场景保住身份。
            RememberId(item.Path, item.Id);
            LogParseIssues(item.Path, note.ParseIssues);

            notes.Add(note);
        }

        _logger.LogInformation("扫描完成，加载了 {Count} 条便签。", notes.Count);
        return notes;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 文件被外部删掉时返回 <see langword="null"/>；文件被独占锁定、重试三次仍读不出来时抛
    /// <see cref="NoteTemporarilyLockedException"/>——这两种情况的处理方式完全不同：
    /// 前者意味着便签没了，后者意味着便签还在、只是这一刻读不到，调用方必须保留内存里的旧内容。
    /// 用 <see langword="null"/> 表达后者会让便签从 NoteStore 里消失（§5.10）。
    /// </para>
    /// <para>
    /// 本方法<strong>不</strong>回写文件。外部编辑是用户的动作，我们只同步内存，
    /// 反过来立刻写回会和用户的编辑器抢文件（§10.3）。
    /// </para>
    /// </remarks>
    public async Task<Note?> ReloadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] raw;
        try
        {
            raw = await ReadAllBytesWithRetryAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "重新读取便签失败，文件可能被其他进程占用：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            throw NoteTemporarilyLockedException.ForPath(path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "重新读取便签失败，没有访问权限：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            throw NoteTemporarilyLockedException.ForPath(path, ex);
        }

        ParsedNoteFile parsed = FrontMatterParser.Parse(raw);

        // 身份优先取文件里的 id。文件被外部编辑器清空了 Front Matter 时，
        // 退回上一次记住的 id：外部编辑改的是内容，不该让便签换一个身份（§5.5）。
        Guid id = parsed.Result.Id
            ?? (_states.TryGetValue(path, out NoteFileState? state) ? state.LastId : Guid.NewGuid());

        var note = new Note
        {
            Id = id,
            FilePath = path,
            Content = parsed.Result.Content,
            Color = parsed.Result.Color ?? DefaultColor,
            Tags = [.. parsed.Result.Tags],
            CreatedAt = parsed.Result.CreatedAt ?? ResolveFileCreatedAt(path),
            UpdatedAt = parsed.Result.UpdatedAt ?? ResolveFileUpdatedAt(path),
            LineEnding = parsed.Result.LineEnding,
            HadBom = parsed.Result.HadBom,
            FrontMatterTail = parsed.Result.FrontMatterTail,
            UnknownFrontMatterKeys = [.. parsed.Result.UnknownFrontMatterKeys],
            ParseIssues = [.. parsed.Result.ParseIssues],
        };

        if (parsed.Result.Id is null)
        {
            note.ParseIssues.Add(new NoteParseIssue(
                NoteParseIssueKind.MissingId,
                "外部编辑后的文件里没有 id，已在内存中沿用原 id；下次保存时会写回。"));
        }

        LogParseIssues(path, note.ParseIssues);
        Cache(path, parsed.Encoding, raw, id);

        return note;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// §5.9 的写前自检在这里：拿「将要写出的字节」与<strong>读入时的原始字节</strong>比哈希，
    /// 相同就整个跳过。用户打开便签看一眼再关掉、或者把改动又改回原样，磁盘上一个字节都不会动。
    /// </para>
    /// <para>
    /// 比的是读入时那份字节，<strong>不是</strong>磁盘此刻的字节。差别在「便签没有被改动，
    /// 但文件被外部程序改过」这一种情况：比磁盘此刻的字节会判定「不一样」，于是把内存里的旧内容
    /// 写回去，正好覆盖掉用户刚在外部做的修改；比读入时的字节则判定「没变化、跳过」，
    /// 是安全的那一侧。
    /// </para>
    /// </remarks>
    public async Task SaveAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        NoteEncodingProfile profile = _states.TryGetValue(note.FilePath, out NoteFileState? state)
            ? state.Encoding
            : note.HadBom ? NoteEncodingProfile.Utf8WithBom : NoteEncodingProfile.Utf8;

        byte[] bytes = FrontMatterSerializer.Serialize(note, profile);
        byte[] hash = SHA256.HashData(bytes);

        if (state is not null && state.ContentHash.AsSpan().SequenceEqual(hash))
        {
            return;
        }

        await _writer.WriteAsync(note.FilePath, bytes, note.UpdatedAt, ct).ConfigureAwait(false);

        // 写成功才更新哈希：失败还更新的话，下一次保存会误判成「内容没变」而永远不重试。
        Cache(note.FilePath, profile, bytes, note.Id);
    }

    // ---- 扫描 ----

    /// <summary>
    /// 递归枚举笔记目录下的全部便签文件（§5.7）。
    /// </summary>
    /// <remarks>
    /// 跳过三类：以 <c>.</c> 开头的目录（<c>.lumimemo</c>、用户的 <c>.obsidian</c>、<c>.git</c>）、
    /// 保留的附件目录、以及 <c>.lumitmp</c> 临时文件。
    /// 目录读取失败（权限、路径过长）只跳过该目录，不让整次扫描失败。
    /// </remarks>
    private IEnumerable<string> EnumerateNoteFiles(string root)
    {
        string attachments = _paths.AttachmentsDirectory;
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
            catch (IOException ex)
            {
                _logger.LogWarning("跳过无法读取的目录：{Directory}（{ExceptionType}）。", directory, ex.GetType().Name);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("跳过没有访问权限的目录：{Directory}（{ExceptionType}）。", directory, ex.GetType().Name);
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                if (Path.GetFileName(subdirectory).StartsWith('.'))
                {
                    continue;
                }

                if (string.Equals(subdirectory, attachments, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            foreach (string file in files)
            {
                if (file.EndsWith(AtomicFileWriter.TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.EndsWith(NoteFileNameBuilder.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>读一个文件并解析。读不出来时返回 <see langword="null"/> 并记日志（§5.10）。</summary>
    private async Task<PendingNote?> TryReadPendingNoteAsync(string path, CancellationToken ct)
    {
        if (path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            _logger.LogWarning("跳过文件名非法的便签：{Path}。", path);
            return null;
        }

        byte[] raw;
        try
        {
            raw = await ReadAllBytesWithRetryAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // 枚举与读取之间文件被删了，正常竞争，不必打扰用户。
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "跳过被占用而读不出来的便签：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "跳过没有访问权限的便签：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            return null;
        }

        ParsedNoteFile parsed = FrontMatterParser.Parse(raw);

        // 编码形态必须在读的时候立刻记下来。等到保存时再去「猜」会猜错：
        // 手上只有 HadBom 一个标志，猜出来的是 UTF-8，于是 GBK 文件会被整份改写成
        // UTF-8——正是 §5.9 明令禁止的那件事。
        Cache(path, parsed.Encoding, raw, parsed.Result.Id ?? Guid.Empty);

        return new PendingNote
        {
            Path = path,
            Parsed = parsed,
            Raw = raw,
            CreatedAt = parsed.Result.CreatedAt ?? ResolveFileCreatedAt(path),
            UpdatedAt = parsed.Result.UpdatedAt ?? ResolveFileUpdatedAt(path),
        };
    }

    /// <summary>读取全部字节，遇到独占锁定按 §5.10 的节奏重试。</summary>
    /// <remarks>
    /// <c>File.ReadAllBytes</c> 以 <c>FileShare.Read</c> 打开文件，别人的独占锁会让它抛
    /// <see cref="IOException"/>——正是需要重试的那一类失败。
    /// </remarks>
    private Task<byte[]> ReadAllBytesWithRetryAsync(string path, CancellationToken ct)
    {
        string ensured = LongPath.Ensure(path);
        return StorageRetry.RunAsync(() => File.ReadAllBytes(ensured), _retryDelays, ct);
    }

    // ---- id 补给（§5.5） ----

    /// <summary>
    /// 给缺 id 的便签生成 id、给冲突的重新分配，并标记出哪些需要回写。
    /// </summary>
    /// <remarks>
    /// 冲突按<strong>文件路径</strong>做 <see cref="StringComparer.OrdinalIgnoreCase"/> 排序后
    /// 保留第一个。不用修改时间：路径是确定的、可复现的，同一台机器上跑多少次结果都一样；
    /// 按修改时间则会在同步工具「碰」一下文件之后换一个胜者，便签身份就在设备之间漂移了。
    /// </remarks>
    private static void ResolveIds(List<PendingNote> pending)
    {
        foreach (PendingNote item in pending)
        {
            if (item.Parsed.Result.Id is { } id)
            {
                item.Id = id;
            }
            else
            {
                item.Id = Guid.NewGuid();
                item.NeedsBackfill = true;
                item.BackfillIssue = new NoteParseIssue(NoteParseIssueKind.MissingId, "文件里没有 id，已生成新 id 并回写。");
            }
        }

        IEnumerable<IGrouping<Guid, PendingNote>> duplicates = pending
            .GroupBy(item => item.Id)
            .Where(group => group.Count() > 1);

        foreach (IGrouping<Guid, PendingNote> group in duplicates)
        {
            List<PendingNote> ordered = [.. group.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)];

            // 第一个保留原 id，其余全部重新生成——三个以上撞在一起时也只留一个。
            for (int i = 1; i < ordered.Count; i++)
            {
                PendingNote loser = ordered[i];
                loser.Id = Guid.NewGuid();
                loser.NeedsBackfill = true;
                loser.BackfillIssue = new NoteParseIssue(
                    NoteParseIssueKind.DuplicateId,
                    "与其他文件共用了同一个 id，已重新分配并回写。");
            }
        }
    }

    /// <summary>把新分配的 id 写回文件；Front Matter 读不懂的先备份（§5.5、§5.10）。</summary>
    private async Task BackfillIdsAsync(List<PendingNote> pending, CancellationToken ct)
    {
        foreach (PendingNote item in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (!item.NeedsBackfill)
            {
                continue;
            }

            if (item.Parsed.FrontMatterUnparsable)
            {
                // 顺序不能反：备份成功才允许改写。备份失败就干脆不动这个文件——
                // 那段读不懂的 Front Matter 至少在用户手里还有一份原样的（§5.10）。
                if (!TryBackupOriginal(item.Path))
                {
                    _logger.LogWarning("备份失败，已放弃修复该文件：{Path}。", item.Path);
                    continue;
                }
            }

            Note note = CreateNote(item);
            byte[] bytes = FrontMatterSerializer.Serialize(note, item.Parsed.Encoding);

            try
            {
                await _writer.WriteAsync(item.Path, bytes, note.UpdatedAt, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    "回写 id 失败，本次启动沿用内存中的 id：{Path}（{ExceptionType}）。",
                    item.Path,
                    ex.GetType().Name);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    "回写 id 失败，文件可能是只读的：{Path}（{ExceptionType}）。",
                    item.Path,
                    ex.GetType().Name);
                continue;
            }

            item.WrittenBack = true;
            Cache(item.Path, item.Parsed.Encoding, bytes, item.Id);
            _logger.LogInformation("已为便签补写 id：{Path}。", item.Path);
        }
    }

    /// <summary>
    /// 把原文件另存一份 <c>.bak</c>（§5.10）。
    /// </summary>
    /// <remarks>
    /// 命名取 §5.10 正文里的 <c>{原文件名}.{yyyyMMddHHmmss}.bak</c>。
    /// 同节的表格里写的是 <c>xxx.md.bak-&lt;时间戳&gt;</c>，两处不一致；取正文那份，
    /// 因为它更具体，而且后缀是 <c>.bak</c> 时文件仍以 <c>.md</c> 结尾的邻居排在一起，
    /// 用户更容易辨认。
    /// </remarks>
    private bool TryBackupOriginal(string path)
    {
        string timestamp = _clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string backup = $"{path}.{timestamp}.bak";

        try
        {
            File.Copy(LongPath.Ensure(path), LongPath.Ensure(backup), overwrite: false);
            _logger.LogWarning("Front Matter 无法解析，已备份原文件：{Backup}。", backup);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning("备份原文件失败：{Path}（{ExceptionType}）。", path, ex.GetType().Name);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("备份原文件失败：{Path}（{ExceptionType}）。", path, ex.GetType().Name);
            return false;
        }
    }

    // ---- 组装 ----

    private Note CreateNote(PendingNote item) => new()
    {
        Id = item.Id,
        FilePath = item.Path,
        Content = item.Parsed.Result.Content,
        Color = item.Parsed.Result.Color ?? DefaultColor,
        Tags = [.. item.Parsed.Result.Tags],
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        LineEnding = item.Parsed.Result.LineEnding,
        HadBom = item.Parsed.Result.HadBom,
        FrontMatterTail = item.Parsed.Result.FrontMatterTail,
        UnknownFrontMatterKeys = [.. item.Parsed.Result.UnknownFrontMatterKeys],
        ParseIssues = [.. item.Parsed.Result.ParseIssues],
    };

    /// <summary>
    /// 取文件系统时间戳作为 <c>createdAt</c> 的兜底（§5.10）。
    /// </summary>
    /// <remarks>
    /// 用<strong>本地</strong>时间而不是 UTC：这个值会参与新便签的文件名
    /// （<c>yyyyMMdd</c>，§5.6），用 UTC 会让东八区凌晨创建的便签被打上前一天的日期。
    /// 取不到像样的创建时间（有些文件系统返回 1601 年）时退回修改时间。
    /// </remarks>
    private DateTimeOffset ResolveFileCreatedAt(string path) =>
        ReadFileTime(path, static info => info.CreationTime)
        ?? ReadFileTime(path, static info => info.LastWriteTime)
        ?? _clock.Now;

    private DateTimeOffset ResolveFileUpdatedAt(string path) =>
        ReadFileTime(path, static info => info.LastWriteTime)
        ?? ReadFileTime(path, static info => info.CreationTime)
        ?? _clock.Now;

    private static DateTimeOffset? ReadFileTime(string path, Func<FileInfo, DateTime> selector)
    {
        try
        {
            DateTime value = selector(new FileInfo(LongPath.Ensure(path)));
            return value.Year < MinPlausibleTimestampYear ? null : new DateTimeOffset(value);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LogParseIssues(string path, List<NoteParseIssue> issues)
    {
        foreach (NoteParseIssue issue in issues)
        {
            // 只记种类与文件路径。正文与 Front Matter 原文一律不进日志（§19.5）：
            // YamlDotNet 的异常消息里会内嵌出错的那几行原文，那正是用户的笔记内容。
            _logger.LogWarning("便签解析降级：{Path}，种类 {Kind}。", path, issue.Kind);
        }
    }

    private void Cache(string path, NoteEncodingProfile encoding, byte[] content, Guid id) =>
        _states[path] = new NoteFileState(encoding, SHA256.HashData(content), id);

    /// <summary>只更新记住的 id，不动编码与哈希。</summary>
    private void RememberId(string path, Guid id)
    {
        if (_states.TryGetValue(path, out NoteFileState? state))
        {
            _states[path] = state with { LastId = id };
        }
    }

    // ---- 内部类型 ----

    /// <summary>一个文件在磁盘上的形态，按路径缓存（§5.9）。</summary>
    /// <param name="Encoding">编码与 BOM 形态，写回时沿用。</param>
    /// <param name="ContentHash">
    /// 读入或写出时那份字节的 SHA-256。保存前拿它做 §5.9 的自检。
    /// </param>
    /// <param name="LastId">
    /// 这个路径最近一次对应的便签 id。用于「文件 Front Matter 被外部清空」时保住身份（§5.5）。
    /// </param>
    private sealed record NoteFileState(NoteEncodingProfile Encoding, byte[] ContentHash, Guid LastId);

    /// <summary>扫描中途的一条便签。id 要到补给流程跑完才最终确定。</summary>
    private sealed class PendingNote
    {
        public required string Path { get; init; }

        public required ParsedNoteFile Parsed { get; init; }

        public required byte[] Raw { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; init; }

        public Guid Id { get; set; }

        /// <summary>磁盘上的 id 与内存中的最终 id 不一致，需要回写。</summary>
        public bool NeedsBackfill { get; set; }

        /// <summary>回写成功后置位，决定 <see cref="NoteParseIssueKind.MissingId"/> 还要不要留在便签上。</summary>
        public bool WrittenBack { get; set; }

        public NoteParseIssue? BackfillIssue { get; set; }
    }
}
