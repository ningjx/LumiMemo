using System.Windows;
using System.Windows.Threading;
using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IDispatcher"/> 的生产实现，直接转发到 WPF 的 <see cref="Dispatcher"/>（§21.1）。
/// </summary>
/// <remarks>
/// <para>
/// 这层包装在运行时是零成本的转发，唯一的价值是让 ViewModel 不写死
/// <c>Application.Current.Dispatcher</c>——测试进程里没有 <see cref="Application"/>，
/// 那句会直接空引用（§21.1）。
/// </para>
/// <para>
/// 无参构造函数默认取当前应用程序的 Dispatcher。它只在 UI 线程上构造才正确，
/// 而组合根本来就跑在 UI 线程上（<c>App.OnStartup</c>），所以是安全的。
/// </para>
/// </remarks>
public sealed class WpfDispatcher : IDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>使用当前 WPF 应用程序的 Dispatcher。</summary>
    public WpfDispatcher()
        : this((Application.Current?.Dispatcher) ?? Dispatcher.CurrentDispatcher)
    {
    }

    /// <param name="dispatcher">要包装的 Dispatcher。仅测试需要显式传入。</param>
    public WpfDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public void VerifyAccess() => _dispatcher.VerifyAccess();

    /// <inheritdoc />
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 已在 UI 线程时直接执行，省掉一次消息泵往返。
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return _dispatcher.InvokeAsync(action).Task;
    }
}
