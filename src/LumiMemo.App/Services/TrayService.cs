using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;

namespace LumiMemo.App.Services;

/// <summary>
/// 托盘图标与右键菜单的宿主，兼「关掉管理器窗口时收进托盘」这条策略（§15.9）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它只做不可测的那一半。</strong> <see cref="TaskbarIcon"/> 要真实的消息循环，
/// 测试进程里建不出来，所以菜单长什么样、图标怎么画在这里；菜单<em>点了做什么</em>
/// 全在 <see cref="TrayViewModel"/> 上，那一半有用例钉着。
/// </para>
/// <para>
/// <strong>菜单在代码里建，不走 XAML。</strong> <see cref="TaskbarIcon"/> 不在任何窗口的
/// 可视树上，把它声明成 <c>App.xaml</c> 的资源就得靠资源查找去够到 DI 造出来的
/// <see cref="TrayViewModel"/>，那条路只会得到一个谁也读不懂的绑定。命令是现成的对象，
/// 直接赋给 <c>MenuItem.Command</c> 就够了。
/// </para>
/// <para>
/// <strong>关闭策略为什么在这里。</strong> 「收进托盘」这件事的两端是托盘图标与管理器窗口，
/// 而它们是同一件事的两面：没有图标就没有地方收，收进去就再也叫不出来。
/// 让窗口自己去读设置的话，它还得知道「图标到底建起来没有」。
/// </para>
/// </remarks>
public sealed class TrayService : IDisposable
{
    /// <summary>便签标题栏那层黄（§15.3），用作图标底色。</summary>
    private const string IconBackground = "#FFF7E9A0";

    private readonly TrayViewModel _viewModel;
    private readonly ManagerWindow _managerWindow;

    private TaskbarIcon? _icon;
    private bool _disposed;

    public TrayService(TrayViewModel viewModel, ManagerWindow managerWindow)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(managerWindow);

        _viewModel = viewModel;
        _managerWindow = managerWindow;

        _managerWindow.Closing += OnManagerWindowClosing;
    }

    /// <summary>
    /// 关闭管理器窗口时收进托盘，而不是退出程序（§8.2 的 <c>minimizeToTrayOnClose</c>）。
    /// </summary>
    /// <remarks>
    /// 可写属性而不是构造参数，与 <c>AutoSaveService.DelayMilliseconds</c> 同一手法。
    /// </remarks>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// 是否显示托盘图标（§8.2 的 <c>showTrayIcon</c>）。
    /// </summary>
    /// <remarks>
    /// <strong>它只在 <see cref="Start"/> 之前改才有意义</strong>：图标建起来之后
    /// 再关掉它，用户就没有任何入口了。设置窗口本轮也没暴露这一项。
    /// </remarks>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>建好图标与菜单，挂上事件。</summary>
    /// <remarks>
    /// 必须在 UI 线程上调用（<c>App.OnStartup</c> 里就是）。
    /// 重复调用是空的——启动路径将来若多一个入口，不会挂出两个图标。
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_icon is not null || !ShowTrayIcon)
        {
            return;
        }

        var icon = new TaskbarIcon
        {
            IconSource = BuildIconSource(),
            ToolTipText = _viewModel.ToolTipText,
            MenuActivation = PopupActivationMode.RightClick,
            ContextMenu = BuildMenu(),
            // 名字里没有 "On"：MVVM 工具包生成命令名时会把方法名的 On 前缀剥掉
            // （OnSingleClick → SingleClickCommand）。这一步由生成器完成，源码里看不出来。
            LeftClickCommand = _viewModel.SingleClickCommand,
            DoubleClickCommand = _viewModel.DoubleClickCommand,
        };

        icon.TrayContextMenuOpen += OnTrayMenuOpen;

        _icon = icon;

        // 关掉效率模式：那是给「真的常驻后台、没有窗口」的程序准备的，
        // 而本程序随时会弹出十几张便签窗口，被 Windows 降频之后拖窗口会卡。
        icon.ForceCreate(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _managerWindow.Closing -= OnManagerWindowClosing;

        if (_icon is not null)
        {
            _icon.TrayContextMenuOpen -= OnTrayMenuOpen;

            // 只 Dispose 就够：它会连同底下的消息窗口一起收掉，
            // 不销毁的话图标会以「幽灵」形态留在通知区域里，直到鼠标划过才消失。
            _icon.Dispose();
            _icon = null;
        }
    }

    /// <summary>
    /// 菜单弹出前把回收站的条目数刷新一遍（§15.9 的「回收站（N）」）。
    /// </summary>
    /// <remarks>
    /// 挂在弹出这一刻，而不是每次删除/恢复之后逐个通知：后者要让回收站那边
    /// 反向认识托盘，而用户看不到菜单的时候那个数字没人看，多算几次没有代价。
    /// </remarks>
    private void OnTrayMenuOpen(object? sender, RoutedEventArgs e) =>
        _ = _viewModel.RefreshTrashCountAsync();

    private void OnManagerWindowClosing(object? sender, CancelEventArgs e)
    {
        // 图标没建起来时不能收：收进去之后没有任何地方能把它叫回来。
        // 这条组合下唯一合理的行为就是让关闭照常发生，也就是退出。
        if (!MinimizeToTrayOnClose || _icon is null)
        {
            return;
        }

        // 取消关闭、改为隐藏。这里必须是 Hide 而不是 Close——WPF 的窗口一旦 Close 过
        // 就不能再 Show，「托盘菜单 → 便签列表」会直接抛 InvalidOperationException。
        //
        // 程序真的要退出时（托盘菜单的「退出」、系统注销）走的是 Application.Shutdown，
        // 那条路忽略 Closing 里的取消，因此这一行不会把退出拦下来。
        e.Cancel = true;

        _managerWindow.Hide();
    }

    /// <summary>
    /// 画一个字母图标。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="GeneratedIconSource"/> 而不是打包一个 <c>.ico</c>：托盘只有 16×16，
    /// 一个带底色的字母在深色与浅色任务栏上都认得出，而多一个二进制资源就多一份
    /// 「这个图标是谁在什么时候放进来的」的维护成本。
    /// 字体必须显式指定——它的默认值是图标字体（Segoe Fluent Icons），
    /// 写字母会得到一串豆腐块。
    /// </remarks>
    private static ImageSource BuildIconSource() => new GeneratedIconSource
    {
        Text = "L",
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IconBackground)),
        Foreground = Brushes.Black,
        FontFamily = new FontFamily("Segoe UI"),
        FontWeight = FontWeights.Bold,
        CornerRadius = new CornerRadius(24),
    };

    /// <summary>按 §15.9 排出来的右键菜单。</summary>
    /// <remarks>
    /// <strong>「速记」本轮不出现</strong>：速记浮窗（§15.7）还没做，
    /// 摆一个点不动的菜单项比不摆更糟——与 §15.8 里那三档视图切换按钮同一个理由。
    /// </remarks>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("新建便签", _viewModel.NewNoteCommand));
        menu.Items.Add(Item("显示全部便签", _viewModel.ShowAllNotesCommand));
        menu.Items.Add(Item("收起全部便签", _viewModel.HideAllNotesCommand));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("便签列表...", _viewModel.OpenManagerCommand));

        // 文字随条目数变，所以是绑定而不是固定字符串。显式给 Source：
        // 这棵菜单不在任何可视树上，靠 DataContext 继承是够不到的。
        MenuItem trash = Item(string.Empty, _viewModel.OpenTrashCommand);
        trash.SetBinding(
            HeaderedItemsControl.HeaderProperty,
            new Binding(nameof(TrayViewModel.TrashMenuHeader)) { Source = _viewModel });
        menu.Items.Add(trash);

        menu.Items.Add(Item("重新加载全部便签", _viewModel.ReloadAllCommand));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("设置...", _viewModel.OpenSettingsCommand));
        menu.Items.Add(Item("打开笔记文件夹", _viewModel.OpenNotesFolderCommand));
        menu.Items.Add(Item("关于", _viewModel.AboutCommand));
        menu.Items.Add(Item("退出", _viewModel.ExitCommand));

        return menu;
    }

    private static MenuItem Item(string header, ICommand command) =>
        new() { Header = header, Command = command };
}
