using LumiMemo.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace LumiMemo.WinUI.Services;

/// <summary><see cref="IUiTimerFactory"/> 的 WinUI 实现：DispatcherQueue 上的一次性定时器。</summary>
/// <remarks>
/// 契约与 Core 的文档一一对应：每次 <c>Start</c> 之后最多回调一次；重复 <c>Start</c> 表示
/// 重新计时（去抖的合并语义）。<c>DispatcherQueueTimer</c> 的 <c>IsRepeating = false</c>
/// 加「停掉再改 Interval」正好是这两个语义的现成组合。
/// </remarks>
public sealed class DispatcherQueueUiTimerFactory(DispatcherQueue queue) : IUiTimerFactory
{
    public IUiTimer Create() => new DispatcherQueueUiTimer(queue);

    private sealed class DispatcherQueueUiTimer : IUiTimer
    {
        private readonly DispatcherQueue _queue;
        private DispatcherQueueTimer? _timer;
        private Action? _onTick;
        private bool _disposed;

        public DispatcherQueueUiTimer(DispatcherQueue queue)
        {
            ArgumentNullException.ThrowIfNull(queue);
            _queue = queue;
        }

        public void Start(TimeSpan interval, Action onTick)
        {
            ArgumentNullException.ThrowIfNull(onTick);
            ObjectDisposedException.ThrowIf(_disposed, this);

            _onTick = onTick;

            if (_timer is null)
            {
                _timer = _queue.CreateTimer();
                _timer.IsRepeating = false;
                _timer.Tick += OnTick;
            }

            // 重新计时：正在跑的旧一轮先停掉（Stop 对未启动的实例也是安全的）。
            _timer.Stop();
            _timer.Interval = interval;
            _timer.Start();
        }

        public void Stop() => _timer?.Stop();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }

            _onTick = null;
        }

        private void OnTick(DispatcherQueueTimer sender, object args) => _onTick?.Invoke();
    }
}
