using System.IO;
using LumiMemo.App.Abstractions;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Events;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// 把监听器报上来的原始文件事件去抖、读盘、封送，交给界面层（§10.2、§10.4、§10.6）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它不做任何业务判断</strong>：谁该开窗、谁该关窗、两边都改了算不算冲突，
/// 全是 <see cref="IExternalChangeSink"/> 后面那个 <c>ManagerViewModel</c> 的事（§14.1）。
/// 这里只把「一条条原始事件」整理成「一批批带内容的结论」。
/// </para>
/// <para>
/// <strong>去抖用的是单一定时器的全局合并窗口</strong>，而不是 §10.2 写的「按路径一棵 CTS」。
/// 两个理由：其一，<c>Task.Delay</c> 在无消息泵的测试进程里没法确定性地驱动，
/// 而本仓库为「连打 5 个字符只写一次盘」这条断言专门立了 <see cref="IUiTimerFactory"/> 端口；
/// 其二，一份合并窗口天然把批量操作（git 切分支、同步盘刷一批）合成一次处理，
/// 与 §10.4 自己规定的「1 秒合并窗口」是同一个道理。
/// 代价是<strong>持续的写入流会把处理一直推到下一次静默</strong>——批量场景下这正是想要的，
/// 单文件反复改动时最多推迟 <see cref="DebounceMilliseconds"/>。
/// </para>
/// <para>
/// <strong>自写抑制不用时间窗口</strong>（对 §10.3 的实质偏离，理由见文档 §10 末尾的实现说明）：
/// 判据只有一条——这次读到的字节是否等于本程序上次同步该路径时的字节。
/// 那件事在仓储层（<c>MarkdownNoteRepository.ReloadAsync</c> 的 <c>DiskChanged</c>）已经算好，
/// 这里只负责把为假的项丢掉。
/// </para>
/// </remarks>
public sealed class FileWatchService : IDisposable
{
    /// <summary>合并窗口。这么长是为了让一次连续操作（拖拽保存、多文件替换）合成一批。</summary>
    public const int DebounceMilliseconds = 300;

    /// <summary>缓冲区溢出之后的合并窗口（§10.4 指定的 1 秒）。</summary>
    /// <remarks>
    /// 比 <see cref="DebounceMilliseconds"/> 长，因为溢出往往伴随一大批刚落地的文件：
    /// 等它们安静下来再扫一遍，比 300 毫秒后扫到一半又被打断强。
    /// </remarks>
    public const int OverflowDebounceMilliseconds = 1000;

    private readonly IFileWatcher _watcher;
    private readonly INoteRepository _repository;
    private readonly IExternalChangeSink _sink;
    private readonly IDispatcher _dispatcher;
    private readonly IUiTimer _debounce;
    private readonly ILogger<FileWatchService> _logger;

    /// <summary>待处理的路径，按进队顺序。顺序有含义，见 <see cref="OnChangeOnUiThread"/>。</summary>
    private readonly List<string> _pendingPaths = [];

    /// <summary>进队去重用的。与 <see cref="_pendingPaths"/> 一一对应（忽略大小写，Windows 的路径语义）。</summary>
    private readonly HashSet<string> _pendingLookup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否有一场缓冲区溢出在等着重扫（§10.4）。</summary>
    private bool _needsFullRescan;

    private bool _isStarted;

    /// <summary>可跨线程读：<see cref="Dispose"/> 走的是退出流程里的线程池线程。</summary>
    private volatile bool _isDisposed;

    public FileWatchService(
        IFileWatcher watcher,
        INoteRepository repository,
        IExternalChangeSink sink,
        IDispatcher dispatcher,
        IUiTimerFactory timers,
        ILogger<FileWatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(logger);

        _watcher = watcher;
        _repository = repository;
        _sink = sink;
        _dispatcher = dispatcher;
        _debounce = timers.Create();
        _logger = logger;
    }

    /// <summary>
    /// 最近一批处理。没有正在跑的一批时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 定时器到期的回调是 <see langword="void"/>，于是「窗口到期 → 读盘 → 封送 → 交给界面」
    /// 这条链的发起处必然发出去就不等。<strong>测试要在 <c>RecordingUiTimer.Fire()</c> 之后
    /// 确定性地等它跑完，唯一的抓手就是这里</strong>——无消息泵的测试进程里，
    /// 外面没有第二个观察得到的点。生产代码不需要读它。
    /// </remarks>
    public Task? PendingWork { get; private set; }

    /// <summary>开始监听。重复调用无效（而不是抛异常或订两遍）。</summary>
    public void Start()
    {
        if (_isDisposed || _isStarted)
        {
            return;
        }

        _isStarted = true;

        // 先订事件再启动：反过来的话，两句话之间到达的变化没人接，
        // 而 §10.4 那种「丢了就永远丢了」的语义下，这一段空档是实打实的漏。
        _watcher.FileChanged += OnFileChanged;
        _watcher.Start();
    }

    /// <summary>
    /// 停止监听并释放内部资源。<strong>不等在途的那一批</strong>。
    /// </summary>
    /// <remarks>
    /// 退出流程里调用，此刻正确的事是尽快松手：在途的那一批读完盘之后会发现
    /// <see cref="_isDisposed"/> 已置位而不往下走（见 <see cref="ProcessAsync"/>），
    /// 它想做的界面改动反正也来不及让用户看见了。
    /// </remarks>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _watcher.FileChanged -= OnFileChanged;
        _debounce.Dispose();
        _pendingPaths.Clear();
        _pendingLookup.Clear();
    }

    /// <summary>
    /// 原始事件。<strong>线程池线程</strong>上到达（§3.4 规则 T3）。
    /// </summary>
    /// <remarks>
    /// 这里只做一件事：把事件甩到 UI 线程上。真正要干的两件事（收集路径、重开去抖定时器）
    /// 都不在这里做——<c>DispatcherTimer</c> 绑死在创建它的那个线程上，
    /// 在线程池线程上碰它直接抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    private void OnFileChanged(object? sender, FileWatchChange change)
    {
        if (_isDisposed)
        {
            return;
        }

        // 发出去就不等，但有两点让它是安全的：一是排进 UI 线程的那段活自己不抛
        // （见 OnChangeOnUiThread），二是退出途中 dispatcher 已经停了，
        // 任务会以取消收场而不是异常。
        _ = _dispatcher.InvokeBackgroundAsync(() => OnChangeOnUiThread(change));
    }

    /// <summary>UI 线程上的那一半：收集路径、重开窗口。</summary>
    private void OnChangeOnUiThread(FileWatchChange change)
    {
        if (_isDisposed)
        {
            return;
        }

        if (change.Kind == FileWatchChangeKind.Error)
        {
            OnOverflow();
            return;
        }

        // 重命名的两个路径都要过一遍，顺序不能反——先新后旧。
        // 「同一 id 换了个路径」要被认成移动（把字段搬进原实例，窗口还绑着它），
        // 反过来的话旧路径先把便签摘走，新路径就找不到那个 id 了，
        // 于是同一张便签换了个 Note 实例，开着的窗口会变成孤儿。
        // 旧路径那一遍还有第二个用处：新文件带着一个全新 id 时（外部编辑器另存为），
        // 它跟旧路径上那张便签对不上，那一张必须有人摘掉。
        Enqueue(change.Path);

        if (change.OldPath is { } oldPath)
        {
            Enqueue(oldPath);
        }

        RestartDebounce();
    }

    /// <summary>
    /// 缓冲区溢出（§10.4）。溢出之后监听器不自愈，这一段是真的丢了。
    /// </summary>
    private void OnOverflow()
    {
        _logger.LogWarning(
            "文件监听缓冲区溢出：这期间的部分变更已被丢弃，稍后重扫整个笔记目录。");

        _needsFullRescan = true;

        _debounce.Start(TimeSpan.FromMilliseconds(OverflowDebounceMilliseconds), OnDebounceElapsed);
    }

    private void Enqueue(string path)
    {
        if (_pendingLookup.Add(path))
        {
            _pendingPaths.Add(path);
        }
    }

    private void RestartDebounce()
    {
        // 溢出已经排在窗口里时，普通事件只进来排队、不重新计时。
        // 否则持续的写入流会把重扫一直往后推，而重扫是「恢复监听」的前置——
        // 那期间监听器还是坏的。
        if (_needsFullRescan)
        {
            return;
        }

        _debounce.Start(TimeSpan.FromMilliseconds(DebounceMilliseconds), OnDebounceElapsed);
    }

    /// <summary>窗口到期：把攒下来的路径一次性交出去。</summary>
    private void OnDebounceElapsed()
    {
        if (_pendingPaths.Count == 0 && !_needsFullRescan)
        {
            return;
        }

        string[] paths = [.. _pendingPaths];
        bool rescan = _needsFullRescan;

        _pendingPaths.Clear();
        _pendingLookup.Clear();
        _needsFullRescan = false;

        PendingWork = ProcessAsync(paths, rescan);
    }

    /// <summary>
    /// 读盘 → 封送回 UI 线程 → 交给界面层。整个过程自己把异常收干净。
    /// </summary>
    /// <remarks>
    /// <strong>发出去就不等的任务必须自己收异常</strong>（与 <c>AutoSaveService.OnTimerDue</c> 同一约定）：
    /// 漏出去的话，外部改动会静默失败，而用户只会看到便签停在旧内容上、还以为同步正常。
    /// </remarks>
    private async Task ProcessAsync(string[] paths, bool rescan)
    {
        try
        {
            if (rescan)
            {
                // 溢出之后先重建监听器，再重扫（§10.4）。
                // 顺序是先重建：从「发现溢出」到「重建完成」这段空档里的事件是真丢，
                // 而重扫补得回「有哪些文件、长什么样」，补不回「刚才谁动过它」。
                _watcher.Stop();
                _watcher.Start();
            }

            if (paths.Length > 0)
            {
                IReadOnlyList<(string Path, NoteFileSync Sync)> batch =
                    await Task.Run(() => ReadAllAsync(paths)).ConfigureAwait(false);

                // 读盘期间程序可能已经在退了，那时不必再把结论往界面上推。
                if (batch.Count > 0 && !_isDisposed)
                {
                    await _dispatcher
                        .InvokeBackgroundAsync(() => _sink.ApplyExternalChangesAsync(batch))
                        .ConfigureAwait(false);
                }
            }

            if (rescan && !_isDisposed)
            {
                await _dispatcher
                    .InvokeBackgroundAsync(_sink.ApplyFullRescanAsync)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理外部文件变更时出错，本批已放弃。");
        }
    }

    /// <summary>
    /// 把一批路径逐个读盘。<strong>跑到线程池线程上</strong>（调用方用 <c>Task.Run</c> 包着）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 读盘不能落在 UI 线程上：网络盘上一次读可能好几秒（§3.4 规则 T6）。
    /// 文件本身很小，所以是一批一次 <c>Task.Run</c>，不是每个文件一次。
    /// </para>
    /// <para>
    /// 单个文件读不出来（被独占锁住、权限不够）不该拖垮整批：那一个跳过、记一笔，
    /// 其余的照常处理。它下次被改动时还会再走一遍这条路。
    /// </para>
    /// </remarks>
    private async Task<List<(string Path, NoteFileSync Sync)>> ReadAllAsync(IReadOnlyList<string> paths)
    {
        var batch = new List<(string Path, NoteFileSync Sync)>(paths.Count);

        foreach (string path in paths)
        {
            try
            {
                NoteFileSync sync = await _repository.ReloadAsync(path).ConfigureAwait(false);

                // 磁盘字节与本程序上次同步这个文件时一模一样——那要么是我们自己刚写的，
                // 要么是一条重复通知。不进管理器：§10.3 的自写抑制就落在这一句上。
                if (sync.DiskChanged)
                {
                    batch.Add((path, sync));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NoteTemporarilyLockedException)
            {
                _logger.LogWarning("外部变更读盘失败，跳过「{Path}」：{Message}", path, ex.Message);
            }
        }

        return batch;
    }
}
