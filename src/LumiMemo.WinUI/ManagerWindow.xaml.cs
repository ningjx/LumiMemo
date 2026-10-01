using H.NotifyIcon;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace LumiMemo.WinUI;

/// <summary>便笺列表：搜索、打开与新建。业务状态在 <see cref="ManagerViewModel"/> 里。</summary>
public sealed partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private AcrylicBackdrop? _backdrop;
    private bool _allowClose;

    public ManagerWindow(ManagerViewModel viewModel, AppSettings settings, ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);

        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;

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

    private async void OnLlmSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await LlmSettingsDialog.ShowAsync(Root.XamlRoot, _settings, _settingsStore);
        }
        catch (Exception exception)
        {
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "设置保存失败",
                Content = exception.Message,
                CloseButtonText = "确定",
            }.ShowAsync();
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideWindow();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        _viewModel.Query = SearchBox.Text;

    private void OnNoteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NoteListItem item)
        {
            _viewModel.OpenNote(item.Note);
        }
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
}
