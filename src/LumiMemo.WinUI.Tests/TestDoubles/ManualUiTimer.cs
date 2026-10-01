using LumiMemo.Core.Abstractions;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>造 <see cref="ManualUiTimer"/> 的工厂：把造出来的每个定时器都记下来。</summary>
public sealed class ManualUiTimerFactory : IUiTimerFactory
{
    /// <summary>造过的定时器，按创建顺序。</summary>
    public List<ManualUiTimer> Created { get; } = [];

    public IUiTimer Create()
    {
        var timer = new ManualUiTimer();
        Created.Add(timer);

        return timer;
    }
}

/// <summary>
/// 可以手动触发到期的 <see cref="IUiTimer"/>：定时逻辑的断言不再依赖真实时间。
/// </summary>
/// <remarks>
/// <see cref="Tick"/> 只在「正在计时」时触发一次（一次性语义与真实实现一致）；
/// 连续 <see cref="Start"/> 会覆盖回调并重新武装。
/// </remarks>
public sealed class ManualUiTimer : IUiTimer
{
    private Action? _onTick;

    /// <summary><see cref="Start"/> 被调了几次（判断「重新计时」用）。</summary>
    public int StartCount { get; private set; }

    /// <summary>当前是否在计时。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>最后一次 <see cref="Start"/> 给的间隔。</summary>
    public TimeSpan? LastInterval { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Start(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);

        _onTick = onTick;
        StartCount++;
        IsRunning = true;
        LastInterval = interval;
    }

    public void Stop() => IsRunning = false;

    /// <summary>手动让这一次计时到期；没在计时时什么也不做。</summary>
    public void Tick()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _onTick?.Invoke();
    }

    public void Dispose()
    {
        IsDisposed = true;
        IsRunning = false;
    }
}
