using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LumiMemo.WinUI.Services;

/// <summary>Owns the WinUI notification-area icon and its currently available commands.</summary>
public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _icon;

    public bool IsCreated => _icon?.IsCreated is true;

    public void Start(
        Action newNote,
        Action showAllNotes,
        Action hideAllNotes,
        Action openManager,
        Action openNotesFolder,
        Action showAbout,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(newNote);
        ArgumentNullException.ThrowIfNull(showAllNotes);
        ArgumentNullException.ThrowIfNull(hideAllNotes);
        ArgumentNullException.ThrowIfNull(openManager);
        ArgumentNullException.ThrowIfNull(openNotesFolder);
        ArgumentNullException.ThrowIfNull(showAbout);
        ArgumentNullException.ThrowIfNull(exitApplication);

        if (_icon is not null)
        {
            return;
        }

        var openManagerCommand = new ActionCommand(openManager);
        var showAllCommand = new ActionCommand(showAllNotes);
        var menu = new MenuFlyout();

        var newItem = new MenuFlyoutItem { Text = "新建便笺" };
        newItem.Click += (_, _) => newNote();
        menu.Items.Add(newItem);

        var showItem = new MenuFlyoutItem { Text = "显示全部便笺" };
        showItem.Click += (_, _) => showAllNotes();
        menu.Items.Add(showItem);

        var hideItem = new MenuFlyoutItem { Text = "收起全部便笺" };
        hideItem.Click += (_, _) => hideAllNotes();
        menu.Items.Add(hideItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var managerItem = new MenuFlyoutItem { Text = "便笺列表..." };
        managerItem.Click += (_, _) => openManager();
        menu.Items.Add(managerItem);

        var folderItem = new MenuFlyoutItem { Text = "打开笔记文件夹" };
        folderItem.Click += (_, _) => openNotesFolder();
        menu.Items.Add(folderItem);

        var aboutItem = new MenuFlyoutItem { Text = "关于" };
        aboutItem.Click += (_, _) => showAbout();
        menu.Items.Add(aboutItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += (_, _) => exitApplication();
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "LumiMemo 鹿米便笺",
            MenuActivation = PopupActivationMode.RightClick,
            ContextFlyout = menu,
            LeftClickCommand = openManagerCommand,
            DoubleClickCommand = showAllCommand,
            IconSource = new GeneratedIconSource
            {
                Text = "L",
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 220, 246)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 55, 71)),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(24)
            }
        };

        _icon.ForceCreate(enablesEfficiencyMode: false);
        if (!_icon.IsCreated)
        {
            _icon.Dispose();
            _icon = null;
            throw new InvalidOperationException("托盘图标注册失败。");
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
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
