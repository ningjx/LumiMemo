using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace LumiMemo.WinUI;

/// <summary>回收站窗口：列出已删除的便笺，恢复或彻底删除。</summary>
public sealed partial class TrashWindow : Window
{
    private readonly TrashViewModel _viewModel;
    private AcrylicBackdrop? _backdrop;
    private bool _allowClose;

    public TrashWindow(TrashViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TrashTitleBar);
        AppWindow.Resize(new SizeInt32(460, 520));
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
    public TrashViewModel ViewModel => _viewModel;

    public async void ShowWindow()
    {
        AppWindow.Show();
        Activate();
        await RefreshAsync();
    }

    public void HideWindow() => AppWindow.Hide();

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _viewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("读取回收站失败", exception.Message, primary: null, close: "确定");
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool empty = _viewModel.Items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled = false;
        PurgeButton.IsEnabled = false;
        PurgeAllButton.IsEnabled = !empty;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = TrashList.SelectedItem is TrashListItem;
        RestoreButton.IsEnabled = selected;
        PurgeButton.IsEnabled = selected;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideWindow();

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (TrashList.SelectedItem is not TrashListItem item)
        {
            return;
        }

        try
        {
            await _viewModel.RestoreAsync(item);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("恢复失败", exception.Message, primary: null, close: "确定");
        }

        UpdateButtons();
    }

    private async void OnPurgeClick(object sender, RoutedEventArgs e)
    {
        if (TrashList.SelectedItem is not TrashListItem item)
        {
            return;
        }

        if (!await ShowDialogAsync(
                $"彻底删除「{item.Title}」？", "删除之后无法恢复。", primary: "删除", close: "取消"))
        {
            return;
        }

        try
        {
            await _viewModel.PurgeAsync(item);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("删除失败", exception.Message, primary: null, close: "确定");
        }

        UpdateButtons();
    }

    private async void OnPurgeAllClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Items.Count == 0)
        {
            return;
        }

        if (!await ShowDialogAsync(
                "清空回收站？",
                $"将彻底删除 {_viewModel.Items.Count} 条便笺，无法恢复。",
                primary: "清空",
                close: "取消"))
        {
            return;
        }

        try
        {
            await _viewModel.PurgeAllAsync();
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("清空失败", exception.Message, primary: null, close: "确定");
        }

        UpdateButtons();
    }

    private async Task<bool> ShowDialogAsync(
        string title, string content, string? primary, string close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = close,
        };

        if (primary is not null)
        {
            dialog.PrimaryButtonText = primary;
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HideWindow();
            return;
        }

        _backdrop?.Dispose();
        _backdrop = null;
    }
}
