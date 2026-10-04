using LumiMemo.WinUI.Controls;
using LumiMemo.WinUI.ViewModels;
using LumiText.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LumiMemo.WinUI.Pages;

/// <summary>回收站页面：列出已删除的便笺，恢复或彻底删除。由壳在切到本页时刷新。</summary>
public sealed partial class TrashPage : UserControl
{
    private readonly TrashViewModel _viewModel;
    private bool _refreshing;

    public TrashPage(TrashViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();

        // 列表也穿同款滚动条（显隐行为见 ScrollBarReveal）。
        TrashList.Loaded += (_, _) => ScrollBarReveal.AttachTo(TrashList);
    }

    /// <summary>XAML 的 x:Bind 从这里取值。</summary>
    public TrashViewModel ViewModel => _viewModel;

    /// <summary>每次显示本页时调：重新读回收站目录。</summary>
    public async Task RefreshAsync()
    {
        // 切页点得快会连着进来好几次：防重入，后到的直接跳过（前一次很快会给出新结果）。
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await _viewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("读取回收站失败", exception.Message, primary: null, close: "确定");
        }
        finally
        {
            _refreshing = false;
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
            XamlRoot = XamlRoot,
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
}
