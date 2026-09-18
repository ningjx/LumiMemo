using LumiMemo.Core.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 造可控定时器的工厂替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>LumiMemo.Core.Tests</c> 里那个 <c>ManualUiTimerFactory</c> 行为相同，
/// 但两边<strong>刻意不共享</strong>：测试工程不互相引用，
/// 而为一个三十行的替身建一个共享工程，代价比重复大得多。
/// </para>
/// <para>
/// 没有它，<c>AutoSaveService</c> 就只能用真实 <c>DispatcherTimer</c> 测——
/// 那个东西在没有消息泵的测试进程里永远不会到期，「500 毫秒后保存一次」
/// 这条断言会变成一个必定超时的等待。
/// </para>
/// </remarks>
public sealed class RecordingUiTimerFactory : IUiTimerFactory
{
    private readonly List<RecordingUiTimer> _timers = [];

    /// <summary>本工厂造出来的全部定时器，按创建顺序。</summary>
    public IReadOnlyList<RecordingUiTimer> Created => _timers;

    /// <summary>最后造出来的那个。</summary>
    /// <exception cref="InvalidOperationException">还没造过任何定时器。</exception>
    public RecordingUiTimer Last =>
        _timers.Count > 0
            ? _timers[^1]
            : throw new InvalidOperationException("还没有创建过定时器，被测服务的构造函数多半没走到。");

    public IUiTimer Create()
    {
        var timer = new RecordingUiTimer();
        _timers.Add(timer);

        return timer;
    }
}

/// <summary>
/// 完全由用例驱动的 <see cref="IUiTimer"/>：不自己计时，只在 <see cref="Fire"/> 时回调。
/// </summary>
public sealed class RecordingUiTimer : IUiTimer
{
    private Action? _onTick;

    /// <summary>最近一次 <see cref="Start"/> 传入的间隔。</summary>
    public TimeSpan? Interval { get; private set; }

    /// <summary>此刻是否在计时。</summary>
    public bool IsRunning { get; private set; }

    /// <summary><see cref="Start"/> 被调了几次。去抖是不是「重新计时」靠它验。</summary>
    public int StartCount { get; private set; }

    /// <summary><see cref="Stop"/> 被调了几次。</summary>
    public int StopCount { get; private set; }

    public void Start(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);

        _onTick = onTick;
        Interval = interval;
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCount++;
    }

    public void Dispose() => IsRunning = false;

    /// <summary>让定时器到期，在调用者的线程上同步执行回调。</summary>
    public void Fire()
    {
        if (!IsRunning)
        {
            return;
        }

        // 一次性：先停再回调。回调里若又调 Start（落盘路径就会），
        // 那一轮必须算作新一轮，不能被这里的状态覆盖掉。
        IsRunning = false;
        _onTick?.Invoke();
    }
}
