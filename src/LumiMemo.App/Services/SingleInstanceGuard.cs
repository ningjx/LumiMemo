using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// 单实例的把门人（§17.2）：认领那把锁，并听第二个实例发来的信号。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么必须单实例</strong>：两个实例同时跑会有两份 <c>layout.json</c> 的写入者
/// 互相覆盖、两个托盘图标。等 <c>FileSystemWatcher</c> 接上之后还会多两个监听者，
/// 那是数据损坏的直接来源。
/// </para>
/// <para>
/// <strong>它不做任何界面动作</strong>。「收到信号之后要干什么」由消费方挂
/// <see cref="SecondInstanceSignalled"/> 决定；这里只负责把信号送到。
/// </para>
/// <para>
/// <strong>与 §17.2 的示例代码不同：这里认的是「命名对象在不在」，不是「锁归谁」。</strong>
/// 文档那段写的是 <c>new Mutex(true, name, out createdNew)</c> 配合 <c>WaitOne</c> 判所有权，
/// 那条路上有两个坑，本类都绕开了：
/// </para>
/// <list type="number">
/// <item>
/// <strong>互斥体的归属属于<em>线程</em>，不是对象。</strong> 用 <c>WaitOne</c> 拿到的锁
/// 必须由同一个线程 <c>ReleaseMutex</c>，换个线程释放会抛
/// <c>ApplicationException: Object synchronization method was called from an unsynchronized block of code</c>。
/// 而在异步代码里「哪条线程执行到 <c>Dispose</c>」根本不由我们说了算
/// （一个 <c>await</c> 之后的续体就可能落在别的线程上）——本类最初的实现正是栽在这里。
/// </item>
/// <item>
/// <strong>上一段描述的难处其实是个伪问题。</strong> 命名对象只要还有句柄开着就存在，
/// 进程一死（不管正常退出还是崩溃）句柄全部关闭、对象随之销毁，
/// 下一个实例拿到的 <c>createdNew</c> 就是 <c>true</c>。<strong>崩溃后的自愈是天然的</strong>，
/// 不需要专门去 catch <c>AbandonedMutexException</c>——那条异常只有在真的
/// <c>WaitOne</c> 一把被放弃的锁时才会出现，而这条路上一把锁都不等。
/// </item>
/// </list>
/// <para>
/// 于是这里只做一件事：<strong>把句柄握住</strong>。<c>initiallyOwned: false</c> 是刻意的——
/// 既然没人会在它上面等待，所有权本身没有意义，留着一个「我占着但从不释放」的锁
/// 只会给后来的人制造困惑。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const int RetryDelayMilliseconds = 50;

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly Mutex _mutex;

    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private bool _disposed;

    /// <param name="mutexName">见 <see cref="SingleInstanceChannel.MutexName"/>。</param>
    /// <param name="pipeName">见 <see cref="SingleInstanceChannel.PipeName"/>。</param>
    public SingleInstanceGuard(string mutexName, string pipeName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeName = pipeName;
        _logger = logger;

        _mutex = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);

        IsFirstInstance = createdNew;
    }

    /// <summary>本进程是不是第一个实例。</summary>
    /// <remarks>
    /// 为 <see langword="false"/> 时调用方应当通知已有实例，然后自己退出。
    /// </remarks>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// 又有一个人启动了程序。
    /// </summary>
    /// <remarks>
    /// <strong>在监听用的后台线程上触发</strong>，不是 UI 线程。消费方要碰界面或
    /// <c>ObservableCollection</c> 就必须先经 <c>IDispatcher.InvokeAsync</c> 封送
    /// （§3.4 规则 T5）。
    /// </remarks>
    public event EventHandler? SecondInstanceSignalled;

    /// <summary>开始听信号。只有第一个实例需要听。</summary>
    /// <exception cref="InvalidOperationException">不是第一个实例。</exception>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsFirstInstance)
        {
            throw new InvalidOperationException(
                "只有第一个实例才需要听第二个实例的信号——第二个实例该做的是发信号然后退出。");
        }

        if (_listenLoop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listenLoop = Task.Run(() => ListenAsync(_cts.Token));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts?.Cancel();

        try
        {
            // 短暂等一下，别让监听循环在进程都开始收尾了还在用已释放的对象。
            // 等不到也无所谓：它下一次拿到取消标记就自己退了。
            _listenLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // 取消导致的异常。这个点已经无处可报，也没有补救动作。
        }

        _cts?.Dispose();
        _cts = null;
        _listenLoop = null;

        // 关掉句柄就够了：命名对象的存亡由句柄数决定，没有锁要释放
        // （也没有线程亲和性可言，因此这个方法在哪个线程上跑都行）。
        _mutex.Dispose();
    }

    /// <summary>收一次握手、扔掉那个管道实例、再建一个新的，直到被取消。</summary>
    /// <remarks>
    /// <para>
    /// <strong>为什么不是「连上就算数」。</strong> 本类最初只等
    /// <c>WaitForConnectionAsync</c>，连上就发事件。那个约定有一个窄窗口：客户端
    /// 连上之后<strong>立刻关掉句柄</strong>时，这个等待会以
    /// <c>IOException: 管道正在被关闭</c> 收场，于是那一次连接变成了异常而不是信号。
    /// 换成「客户端写一个字节、服务端读到才发事件、发完回一个字节让客户端放心走」之后，
    /// 客户端会一直握着句柄等到回音，那个窗口就不存在了。
    /// </para>
    /// <para>
    /// 整个方法<strong>不往外抛</strong>：它跑在一个没人等待的 <c>Task</c> 上，
    /// 逃出去的异常只会变成一条「未观察的 Task 异常」，而监听循环死掉这件事
    /// 在界面上的表现是「再开一个实例时第一个毫无反应」，离原因非常远。
    /// </para>
    /// </remarks>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                byte[] buffer = new byte[sizeof(byte)];

                if (await server.ReadAsync(buffer, ct).ConfigureAwait(false) != buffer.Length)
                {
                    // 连上又走了，一个字节都没写。这一次不算数——继续等下一个。
                    continue;
                }

                // 先回音再发事件：客户端那边读完回音就可以安心退出了，
                // 而事件处理器要做的（把已有实例唤到前台）慢一点也没关系。
                await server.WriteAsync(buffer, ct).ConfigureAwait(false);

                SecondInstanceSignalled?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "单实例的命名管道出了状况，稍后重试。");

                try
                {
                    await Task.Delay(RetryDelayMilliseconds, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
