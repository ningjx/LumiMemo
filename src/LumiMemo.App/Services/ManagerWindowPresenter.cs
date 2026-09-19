using System.Windows;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Views;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IManagerWindowPresenter"/> 的生产实现：操作那个唯一的管理器窗口。
/// </summary>
/// <remarks>
/// <para>
/// <strong>收的是工厂而不是窗口实例</strong>，这是被迫的：
/// <see cref="ViewModels.ManagerViewModel.ShowAll"/> 在一条便签都没打开时要用它
/// 把管理器自己带出来，于是依赖成了
/// <c>ManagerViewModel</c> → 本类 → <c>ManagerWindow</c> → <c>ManagerViewModel</c>，
/// 一个容器解不开的环。换成 <see cref="Func{TResult}"/> 之后这条边在构造期就断了，
/// 而窗口仍然是那个单例——工厂每次返回的都是同一个对象。
/// </para>
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
    private readonly Func<ManagerWindow> _windowFactory;

    public ManagerWindowPresenter(Func<ManagerWindow> windowFactory)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);

        _windowFactory = windowFactory;
    }

    /// <inheritdoc />
    public void BringToFront()
    {
        ManagerWindow window = _windowFactory();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();

        _ = window.Activate();
    }
}
