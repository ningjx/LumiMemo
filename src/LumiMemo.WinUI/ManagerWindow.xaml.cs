using H.NotifyIcon;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace LumiMemo.WinUI;

/// <summary>便笺列表：搜索、打开与新建。业务状态在 <see cref="ManagerViewModel"/> 里。</summary>
public sealed partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly TrashWindow _trashWindow;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private AcrylicBackdrop? _backdrop;
    private bool _allowClose;

    public ManagerWindow(
        ManagerViewModel viewModel,
        TrashWindow trashWindow,
        AppSettings settings,
        ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trashWindow);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);

        InitializeComponent();

        _viewModel = viewModel;
        _trashWindow = trashWindow;
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

        // 初始排序在 ViewModel 赋值之后设置：早于它的话 SelectionChanged 会撞上未初始化的字段。
        SortBox.SelectedIndex = 0;
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

    private void OnTrashClick(object sender, RoutedEventArgs e) => _trashWindow.ShowWindow();

    private void OnItemOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteListItem item)
        {
            _viewModel.OpenNote(item.Note);
        }
    }

    private async void OnItemDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteListItem item)
        {
            return;
        }

        try
        {
            if (!await _viewModel.DeleteNoteAsync(item.Note))
            {
                // false 的含义是「窗口内容还没存下来」——不删除，让用户先处理保存失败。
                await ShowDialogAsync("未删除", "便笺有修改尚未保存成功，已保留原地。请稍后再试。");
            }
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("删除失败", exception.Message);
        }
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "确定",
        }.ShowAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        _viewModel.Query = SearchBox.Text;

    private void OnColorFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked)
        {
            return;
        }

        // 互斥单选：选中项加深描边；「全部」表示不筛颜色。
        foreach (Button button in ColorFilterButtons())
        {
            bool selected = ReferenceEquals(button, clicked);
            button.BorderThickness = new Thickness(selected ? 3 : 1);
            button.BorderBrush = new SolidColorBrush(selected
                ? Color.FromArgb(255, 0x40, 0x37, 0x47)
                : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }

        _viewModel.ColorFilter = clicked.Tag is string name && Enum.TryParse(name, out NoteColor color)
            ? color
            : null;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SortByModifiedTime = SortBox.SelectedIndex == 1;

    private IEnumerable<Button> ColorFilterButtons()
    {
        yield return ColorFilterAll;
        yield return ColorFilterYellow;
        yield return ColorFilterPink;
        yield return ColorFilterBlue;
        yield return ColorFilterGreen;
        yield return ColorFilterPurple;
        yield return ColorFilterOrange;
        yield return ColorFilterGray;
    }

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
