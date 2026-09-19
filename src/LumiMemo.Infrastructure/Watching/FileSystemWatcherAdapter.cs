using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Events;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Watching;

/// <summary>
/// <see cref="IFileWatcher"/> 的实现：把 Windows 的 <c>FileSystemWatcher</c> 包成平台无关的事件（§10.1）。
/// </summary>
/// <remarks>
/// <para>
/// 本类<strong>只做翻译</strong>：不做去抖、不读盘、不碰内存状态。那些在 App 层的
/// <c>FileWatchService</c> 里，因为它们都要在 UI 线程上按既定的批次语义来跑（§10.2）。
/// </para>
/// <para>
/// <strong>只报「扫描器也会收」的文件</strong>。监听器比扫描器松的话，
/// 用户改一下 Obsidian 的配置就会在便签列表里冒出一张重启后又不存在的幽灵便签，
/// 所以过滤交给仓储的 <see cref="INoteRepository.IsNoteFile"/>——两处判据必须是同一套。
/// </para>
/// <para>
/// <see cref="Stop"/> 会把内部的 <c>FileSystemWatcher</c> 整个丢掉。这是有意的：
/// 缓冲区溢出之后实例不会自愈，恢复的办法就是重来一个（§10.4），
/// 于是「重建」在这里就是 <see cref="Stop"/> + <see cref="Start"/>。
/// </para>
/// </remarks>
public sealed class FileSystemWatcherAdapter : IFileWatcher
{
    /// <summary>
    /// 变更缓冲区大小（§10.1）。
    /// </summary>
    /// <remarks>
    /// 默认的 8KB 在一次批量操作（git 切分支、同步盘刷一批文件）下几秒就会溢出，
    /// 而溢出之后的丢失是<strong>静默</strong>的。64KB 是 §10.1 定的值。
    /// </remarks>
    private const int BufferSizeBytes = 64 * 1024;

    private readonly IAppPaths _paths;
    private readonly INoteRepository _repository;
    private readonly ILogger<FileSystemWatcherAdapter> _logger;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public FileSystemWatcherAdapter(
        IAppPaths paths,
        INoteRepository repository,
        ILogger<FileSystemWatcherAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<FileWatchChange>? FileChanged;

    /// <inheritdoc />
    /// <remarks>
    /// 尚未选定笔记目录（首次运行的向导还没走完，§8.6）时是空操作，只记一条日志：
    /// 那不是错误，是启动顺序里的正常状态。笔记目录后来才被选定时，
    /// 需要有人再调一次本方法——目前那意味着重启程序（§8.6 的切换目录同样要求重启）。
    /// </remarks>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
            {
                return;
            }

            string? root = _paths.NotesFolder;
            if (string.IsNullOrWhiteSpace(root))
            {
                _logger.LogWarning("尚未选定笔记目录，本次不启用文件监听。");
                return;
            }

            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    Filter = "*" + NoteFileNameBuilder.Extension,
                    IncludeSubdirectories = true,
                    InternalBufferSize = BufferSizeBytes,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                };

                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;

                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // 笔记目录不在了（移动盘被拔掉、网络盘断开）。启动不该为此失败：
                // 便签内容都还在磁盘上，只是这一次运行看不到外部变化（§11.5）。
                _logger.LogWarning(
                    "无法监听笔记目录，本次运行不感知外部修改：{Root}（{ExceptionType}）。",
                    root,
                    ex.GetType().Name);
                return;
            }

            _watcher = watcher;
            _logger.LogInformation("已开始监听笔记目录：{Root}。", root);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (_watcher is null)
            {
                return;
            }

            // 先退订再释放：退订之后不会再有新的回调进来，
            // 而这个瞬间还在途中的那几个回调即使跑完也无处可去。
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Raise(new FileWatchChange(FileWatchChangeKind.Created, e.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Raise(new FileWatchChange(FileWatchChangeKind.Changed, e.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Raise(new FileWatchChange(FileWatchChangeKind.Deleted, e.FullPath));

    /// <summary>
    /// 重命名或移动。
    /// </summary>
    /// <remarks>
    /// 这里把「移到哪里去了」先判一遍，让消费方拿到的 <see cref="FileWatchChangeKind.Renamed"/>
    /// 只有一种含义：<strong>一个便签文件在笔记目录里换了位置</strong>。
    /// 移出笔记目录（退出时被拖走、被搬到别的盘）与移进来是两件事，
    /// 分别降级成「删除」与「新建」——不这么做的话，消费方要为每种组合再写一遍判断。
    /// 重命名整个文件夹时 <c>Filter</c> 与目录名对不上，收不到事件，因此那里不必考虑。
    /// </remarks>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool oldIsNote = _repository.IsNoteFile(e.OldFullPath);
        bool newIsNote = _repository.IsNoteFile(e.FullPath);

        if (oldIsNote && newIsNote)
        {
            Raise(new FileWatchChange(FileWatchChangeKind.Renamed, e.FullPath, e.OldFullPath));
        }
        else if (oldIsNote)
        {
            Raise(new FileWatchChange(FileWatchChangeKind.Deleted, e.OldFullPath));
        }
        else if (newIsNote)
        {
            Raise(new FileWatchChange(FileWatchChangeKind.Created, e.FullPath));
        }
    }

    /// <summary>
    /// 监听出错，实际上就是缓冲区溢出（§10.4）。
    /// </summary>
    /// <remarks>
    /// 不在这里重扫：重扫要读整目录、要改内存状态，都必须在 UI 线程上按批次来。
    /// 本类只把这件事报出去。
    /// </remarks>
    private void OnError(object sender, ErrorEventArgs e) =>
        Raise(new FileWatchChange(
            FileWatchChangeKind.Error,
            _paths.NotesFolder ?? string.Empty));

    /// <summary>只报扫描器也会收的文件；被排除的路径当作没发生过。</summary>
    private void Raise(FileWatchChange change)
    {
        if (change.Kind != FileWatchChangeKind.Error && !_repository.IsNoteFile(change.Path))
        {
            return;
        }

        FileChanged?.Invoke(this, change);
    }
}
