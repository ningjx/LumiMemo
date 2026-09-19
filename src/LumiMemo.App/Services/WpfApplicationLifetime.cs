using System.Windows;
using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IApplicationLifetime"/> 的生产实现：转交给 WPF 的
/// <see cref="Application.Shutdown()"/>。
/// </summary>
/// <remarks>
/// <para>
/// <strong>只调 <c>Shutdown()</c>，不自己走退出序列。</strong>
/// §17.4 的收尾挂在 <c>App.OnExit</c> 上，<c>Shutdown()</c> 会把它触发出来——
/// 这里再抄一遍的话，「托盘退出」与「系统注销导致退出」就会走两条不同的路，
/// 而后者是没法用手工测试覆盖的，两边一旦不一致就只能靠用户丢数据来发现。
/// </para>
/// <para>
/// <strong>它唯一多做的一件事是先通知窗口层</strong>
/// （<see cref="IWindowManager.BeginShutdown"/>）。那不是收尾，是**顺序**问题：
/// <c>Shutdown()</c> 先关掉每一个便签窗口、再触发 <c>App.OnExit</c>（§17.4），
/// 所以「接下来关的窗口不是用户关的」这句话只能在 <c>Shutdown()</c> <em>之前</em>说。
/// 等挪到 <c>OnExit</c> 里再补，窗口已经全关完了，便签全部被记成已关闭。
/// </para>
/// <para>
/// 取不到 <see cref="Application.Current"/> 时<strong>什么都不做</strong>。
/// 那意味着没有 WPF 应用在跑（无头测试，或者退出已经走到了 <c>OnExit</c> 之后），
/// 此时本来也没有进程可以退，更没有窗口关闭需要重新定性。
/// </para>
/// </remarks>
public sealed class WpfApplicationLifetime : IApplicationLifetime
{
    private readonly IWindowManager _windows;

    public WpfApplicationLifetime(IWindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        _windows = windows;
    }

    /// <inheritdoc />
    public void RequestShutdown()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        _windows.BeginShutdown();

        app.Shutdown();
    }
}
