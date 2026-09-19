using System.Windows;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Views;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IManagerWindowPresenter"/> 的生产实现：操作那个唯一的管理器窗口。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它调 <c>Show()</c> 而不担心「窗口已经关掉了」</strong>：管理器的关闭策略
/// 走的是 <see cref="Window.Hide"/>（见 <c>TrayService</c>），它从来没有被
/// <c>Close()</c> 过，因此可以反复 <c>Show()</c>。若哪天有人把那条策略改成
/// <c>Close()</c>，这里的第二行就会抛 <c>InvalidOperationException</c>——
/// 这不是隐患，是刻意的：让错误在第一次点菜单时就炸出来，而不是变成
/// 「点了几次之后再也打不开」。
/// </para>
/// <para>
/// <strong>未最小化时不去动 <see cref="Window.WindowState"/></strong>：
/// 用户把窗口拖成最大化之后关掉（收进托盘）、再从菜单打开，它应当还是最大化。
/// 无条件写 <c>Normal</c> 会把那件事抹掉。
/// </para>
/// </remarks>
public sealed class ManagerWindowPresenter : IManagerWindowPresenter
{
    private readonly ManagerWindow _window;

    public ManagerWindowPresenter(ManagerWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
    }

    /// <inheritdoc />
    public void BringToFront()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();

        _ = _window.Activate();
    }
}
