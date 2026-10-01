using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LumiMemo.WinUI.Services;

/// <summary>把管理器窗口 XAML 树里的 <see cref="TaskbarIcon"/> 配置成便笺的托盘入口。</summary>
/// <remarks>
/// <para>
/// 控件由宿主窗口提供而不是在这里 new：库内部的消息处理、菜单展开与 DispatcherQueue
/// 调度都依赖它挂在可视化树上，悬空实例会出现「图标在、点了全都没反应」。
/// </para>
/// <para>
/// 管理器没显示时（启动只恢复了便签的场景）窗口的 Loaded 不会触发，因此这里
/// 显式 <c>ForceCreate</c>，让托盘入口在任何启动路径下都可用。
/// </para>
/// </remarks>
public sealed class TrayIconService(TaskbarIcon icon) : IDisposable
{
    /// <summary>装配菜单与命令，并注册图标。</summary>
    public void Start(
        Action newNote,
        Action hideAllNotes,
        Action openManager,
        Action openNotesFolder,
        Action showAbout,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(newNote);
        ArgumentNullException.ThrowIfNull(hideAllNotes);
        ArgumentNullException.ThrowIfNull(openManager);
        ArgumentNullException.ThrowIfNull(openNotesFolder);
        ArgumentNullException.ThrowIfNull(showAbout);
        ArgumentNullException.ThrowIfNull(exitApplication);

        if (icon.IsCreated)
        {
            return;
        }

        // 菜单项必须挂 Command，不能用 Click 事件：库的默认菜单模式把 MenuFlyout
        // 代理成 Win32 原生菜单，点击时只回传 MenuFlyoutItem.Command
        // （TaskbarIcon.ContextMenu.WinRT.PopupMenu.cs 的 Win32 菜单回调里只有
        // Command?.TryExecute）。用 Click 挂的事件在这个模式下永远不会被触发——
        // 症状正是「菜单能弹出来，点任何一项都没有反应」。
        //
        // 菜单里没有「显示全部便笺」：便签数量一多，全铺到桌面上没有意义（2026-10 移除）。
        var menu = new MenuFlyout();

        menu.Items.Add(new MenuFlyoutItem { Text = "新建便笺", Command = new ActionCommand(newNote) });
        menu.Items.Add(new MenuFlyoutItem { Text = "收起全部便笺", Command = new ActionCommand(hideAllNotes) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "便笺列表...", Command = new ActionCommand(openManager) });
        menu.Items.Add(new MenuFlyoutItem { Text = "打开笔记文件夹", Command = new ActionCommand(openNotesFolder) });
        menu.Items.Add(new MenuFlyoutItem { Text = "关于", Command = new ActionCommand(showAbout) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "退出", Command = new ActionCommand(exitApplication) });

        icon.ToolTipText = "LumiMemo 鹿米便笺";
        icon.MenuActivation = PopupActivationMode.RightClick;
        icon.ContextFlyout = menu;
        icon.LeftClickCommand = new ActionCommand(openManager);
        icon.DoubleClickCommand = new ActionCommand(openManager);
        icon.IconSource = new GeneratedIconSource
        {
            Text = "L",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 220, 246)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 55, 71)),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(24),
        };

        // 管理器窗口没显示时 Loaded 不会触发，图标创建要手动推一把。
        icon.ForceCreate(enablesEfficiencyMode: false);

        if (!icon.IsCreated)
        {
            throw new InvalidOperationException("托盘图标注册失败。");
        }
    }

    public void Dispose()
    {
        icon.Dispose();
    }

    private sealed class ActionCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
