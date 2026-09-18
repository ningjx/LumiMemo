using LumiMemo.Core.Abstractions;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 手动到期的一次性定时器替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 真实现（<c>DispatcherTimer</c>）要等消息泵把时间真的走到，测试里既慢又不稳。
/// 这里只把「有没有人在等、等多久、被重启过几次」记下来，由用例决定什么时候到期——
/// 于是「连调 5 次只落盘 1 次」这种节流逻辑是确定性可测的，不必真等满 5 秒。
/// </remarks>
public sealed class ManualUiTimer : IUiTimer
{
    private Action? _onTick;

    /// <summary>最近一次 <see cref="Start"/> 要求的延时。</summary>
    public TimeSpan? Interval { get; private set; }

    /// <summary>当前是否有人在等。到期之后变回 <see langword="false"/>（一次性）。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// <see cref="Start"/> 被调了几次。
    /// </summary>
    /// <remarks>
    /// 用来区分「重新计时」和「排队等多次回调」：去抖要的是前者。
    /// 如果被测代码把回调挂在了别处而不是重启本定时器，这个数字会露馅。
    /// </remarks>
    public int StartCount { get; private set; }

    public void Start(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);

        _onTick = onTick;
        Interval = interval;
        IsRunning = true;
        StartCount++;
    }

    public void Stop() => IsRunning = false;

    public void Dispose() => IsRunning = false;

    /// <summary>让定时器到期，在调用者的线程上同步执行回调。</summary>
    /// <remarks>
    /// 没有在计时时直接返回，与真实定时器一致（停掉的表不会回调）。
    /// 顺序是<strong>先停再回调</strong>：回调里往往会再调 <see cref="Start"/>
    /// （本项目的落盘路径就是这样），那一轮必须从干净状态开始。
    /// </remarks>
    public void Fire()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _onTick?.Invoke();
    }
}
