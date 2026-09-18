using System.Windows.Threading;
using LumiMemo.Core.Abstractions;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IUiTimerFactory"/> 的 WPF 实现，内部就是 <see cref="DispatcherTimer"/>（§11.1）。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="DispatcherTimer"/> 而不是 <c>System.Timers.Timer</c>：它的回调天然在
/// UI 线程上，从根源上避免了跨线程问题（§3.4 规则 T4）。<c>System.Timers.Timer</c>
/// 的回调在线程池线程上，要用它就得自己封送，而封送是这个项目最容易出错的点之一。
/// </para>
/// <para>
/// 调度器由调用方传进来，不在这里读 <c>Application.Current</c>：
/// 隐式取环境里的调度器意味着「在哪构造」变成一条不成文的约定，
/// 而构造在哪个线程上决定了定时器挂在哪个线程上——那正是最该写明白的一件事。
/// 组合根传的是 <c>Application.Current.Dispatcher</c>。
/// </para>
/// </remarks>
public sealed class DispatcherTimerFactory : IUiTimerFactory
{
    private readonly Dispatcher _dispatcher;

    /// <param name="dispatcher">UI 线程的调度器，通常取自 <c>Application.Current.Dispatcher</c>。</param>
    public DispatcherTimerFactory(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public IUiTimer Create() => new DispatcherUiTimer(_dispatcher);

    /// <summary>挂在指定调度器上的一次性定时器。</summary>
    private sealed class DispatcherUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;
        private Action? _onTick;

        public DispatcherUiTimer(Dispatcher dispatcher)
        {
            // Background 优先级：落盘不该抢在输入与渲染前面。Normal 优先级下，
            // 一次写盘能把正在拖动的窗口卡一下，而「拖动不因磁盘 IO 抖动」
            // 恰恰是 §8.5 的性能目标。
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);

            // 只挂一个稳定的处理器，回调本体放在字段里。
            // 每次 Start 都去 Tick 上挂一个 lambda 的话，挂上去的会越积越多，
            // 到时候一次到期会跑完此前每一次的 Start 留下的那个回调。
            _timer.Tick += OnTick;
        }

        /// <summary>到期：<strong>先停表再回调</strong>，把周期性的 DispatcherTimer 用成一次性的。</summary>
        /// <remarks>
        /// <see cref="DispatcherTimer"/> 本身没有「只触发一次」这回事（<see cref="DispatcherTimer.Interval"/>
        /// 一到就反复触发），所以一次性得在这里自己做。顺序也不能反：先停再回调，
        /// 回调里若又调 <see cref="Start"/>（本项目的落盘路径就是这么写的），
        /// 那一轮才是干净的新一轮。
        /// </remarks>
        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            _onTick?.Invoke();
        }

        public void Start(TimeSpan interval, Action onTick)
        {
            ArgumentNullException.ThrowIfNull(onTick);

            _onTick = onTick;

            // 先停再起才是「重新计时」。只改 Interval 而不停的话，
            // 已经在走的那一轮不受影响，去抖就退化成「第一次之后固定间隔」。
            _timer.Stop();
            _timer.Interval = interval;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        /// <summary>停表并摘掉回调。</summary>
        /// <remarks>
        /// 摘回调不只是省一次调用：已经排进调度队列、但还没轮到的那个 Tick
        /// 会持有本对象，摘干净了整条引用链才能一起被回收。
        /// </remarks>
        public void Dispose()
        {
            _timer.Stop();
            _onTick = null;
        }
    }
}
