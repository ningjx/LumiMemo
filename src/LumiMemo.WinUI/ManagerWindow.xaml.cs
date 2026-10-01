using H.NotifyIcon;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Pages;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace LumiMemo.WinUI;

/// <summary>
/// 管理窗口壳：标题栏（新建/设置/回收站/关闭 全图标）、页面容器与右下角计数。
/// </summary>
/// <remarks>
/// 便笺列表 / 回收站 / 设置是三个页面（<see cref="UserControl"/>），
/// 由本窗口叠放并通过 Visibility 切换——切页不重建，搜索词与筛选状态都留在页内。
/// 业务状态在各页自己的 ViewModel 里。
/// </remarks>
public sealed partial class ManagerWindow : Window
{
    private static readonly SolidColorBrush PageSelectedBackground =
        new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush PageSelectedBorder =
        new(Color.FromArgb(0x58, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush TransparentBrush =
        new(Color.FromArgb(0, 0, 0, 0));

    private readonly ManagerViewModel _viewModel;
    private readonly NoteListPage _noteListPage;
    private readonly TrashPage _trashPage;
    private readonly SettingsPage _settingsPage;
    private AcrylicBackdrop? _backdrop;
    private bool _allowClose;
    private ManagerPage _currentPage = ManagerPage.Notes;

    public ManagerWindow(
        ManagerViewModel viewModel,
        TrashViewModel trashViewModel,
        AppSettings settings,
        ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trashViewModel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);

        InitializeComponent();

        _viewModel = viewModel;

        _noteListPage = new NoteListPage(viewModel);
        _trashPage = new TrashPage(trashViewModel);
        _settingsPage = new SettingsPage(settings, settingsStore);
        PageHost.Children.Add(_noteListPage);
        PageHost.Children.Add(_trashPage);
        PageHost.Children.Add(_settingsPage);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ManagerTitleBar);
        AppWindow.Resize(new SizeInt32(520, 620));
        AppWindow.Closing += OnWindowClosing;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = false;
        }

        _backdrop = AcrylicBackdrop.Apply(this, Root);

        ShowPage(ManagerPage.Notes);
    }

    /// <summary>XAML 的 x:Bind 从这里取值。</summary>
    public ManagerViewModel ViewModel => _viewModel;

    /// <summary>托盘控件（挂在 XAML 树里，由 App 在组装托盘入口时配置）。</summary>
    public TaskbarIcon TrayIconHost => TrayIcon;

    public void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    public void HideWindow() => AppWindow.Hide();

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public async Task ShowAboutAsync()
    {
        ShowWindow();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "关于 LumiMemo",
            Content = "鹿米便笺 WinUI 富文本实验版\n便笺保存在本地 .lumi 文件中。",
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    private async void OnNewNoteClick(object sender, RoutedEventArgs e) =>
        await _viewModel.CreateNoteAsync();

    private void OnSettingsPageClick(object sender, RoutedEventArgs e) =>
        ShowPage(_currentPage == ManagerPage.Settings ? ManagerPage.Notes : ManagerPage.Settings);

    private void OnTrashPageClick(object sender, RoutedEventArgs e) =>
        ShowPage(_currentPage == ManagerPage.Trash ? ManagerPage.Notes : ManagerPage.Trash);

    private void OnHideClick(object sender, RoutedEventArgs e) => HideWindow();

    private void ShowPage(ManagerPage page)
    {
        _currentPage = page;

        _noteListPage.Visibility = page == ManagerPage.Notes ? Visibility.Visible : Visibility.Collapsed;
        _trashPage.Visibility = page == ManagerPage.Trash ? Visibility.Visible : Visibility.Collapsed;
        _settingsPage.Visibility = page == ManagerPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        SetPageSelected(SettingsButton, page == ManagerPage.Settings);
        SetPageSelected(TrashButton, page == ManagerPage.Trash);

        switch (page)
        {
            case ManagerPage.Trash:
                // 每次切入都重读回收站目录（别的实例或清理任务可能改过它）。
                _ = _trashPage.RefreshAsync();
                break;
            case ManagerPage.Settings:
                _settingsPage.Reload();
                break;
        }
    }

    /// <summary>页面图标的选中态（当前页亮起；再点一下会回到列表页）。</summary>
    private static void SetPageSelected(Button button, bool selected)
    {
        button.Background = selected ? PageSelectedBackground : TransparentBrush;
        button.BorderBrush = selected ? PageSelectedBorder : TransparentBrush;
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HideWindow();
            return;
        }

        _viewModel.Dispose();
        _backdrop?.Dispose();
        _backdrop = null;
    }

    private enum ManagerPage
    {
        Notes,
        Trash,
        Settings,
    }
}
