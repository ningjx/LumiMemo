using System.Windows;
using LumiMemo.App.Views;

namespace LumiMemo.App.Services;

/// <summary>
/// 打开设置窗口，并保证同一时刻只有一个（§15.9）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么不把 <c>SettingsWindow</c> 注册成单例</strong>：WPF 的
/// <c>Window</c> 一旦 <c>Close()</c> 过就不能再 <c>Show()</c>——
/// 「同一个窗口关掉再开」在 WPF 里不存在，第二次会抛
/// <c>InvalidOperationException</c>。所以每次打开都要一个新的窗口对象，
/// 而「同时只开一个」这条约束必须有人记着，那就是本类。
/// </para>
/// <para>
/// <strong>拿的是 <c>Func&lt;SettingsWindow&gt;</c> 而不是容器</strong>：注入
/// <c>IServiceProvider</c> 会让这个类变成一个服务定位器，谁都看不出它到底需要什么。
/// 一个工厂委托把依赖写在签名上，测试里也能塞一个假窗口进去。
/// </para>
/// <para>
/// 它<strong>不做业务判断</strong>：设置怎么读、怎么存、怎么生效全在
/// <c>SettingsViewModel</c> 里。这里只有窗口的生命周期。
/// </para>
/// </remarks>
public sealed class SettingsWindowLauncher
{
    private readonly Func<SettingsWindow> _create;

    /// <summary>当前开着的那一个；没开时是 <see langword="null"/>。</summary>
    private SettingsWindow? _current;

    public SettingsWindowLauncher(Func<SettingsWindow> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        _create = create;
    }

    /// <summary>
    /// 打开设置窗口。已经开着就把它唤到前面，不再开第二个。
    /// </summary>
    /// <returns>这一次调用真的新开了一个窗口时返回 <c>true</c>。</returns>
    public bool Show()
    {
        if (_current is { } existing)
        {
            // 只还原、不重开：重开会让用户刚填了一半的表单凭空消失。
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            _ = existing.Activate();

            return false;
        }

        SettingsWindow window = _create();

        if (Owner() is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            // 没有宿主时不能留 WindowStartupLocation=CenterOwner：那会在没有 Owner
            // 的情况下把窗口摆到屏幕左上角，而不是默认的居中。
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _current = window;

        // 关掉之后把记录清空，下一次才会新开一个。
        window.Closed += (_, _) => _current = null;

        window.Show();

        return true;
    }

    /// <summary>当前开着的那个窗口（测试与托盘菜单要判断「是否已开」时会问）。</summary>
    public bool IsOpen => _current is not null;

    /// <summary>
    /// 设置窗口该挡在谁前面。
    /// </summary>
    /// <remarks>
    /// 取 <see cref="Application.MainWindow"/>。管理器是本程序唯一的常驻窗口，
    /// <c>StartupSequence</c> 第一个 <c>Show()</c> 的就是它，因此 WPF 会自动把它设为
    /// <c>MainWindow</c>——组合根<strong>刻意没有手工赋值</strong>（那会引入
    /// 「主窗口关闭」的额外隐式语义）。
    /// </remarks>
    private static Window? Owner() =>
        Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;
}
