using LumiMemo.Core.Models;
using LumiMemo.WinUI.Controls;
using LumiMemo.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace LumiMemo.WinUI.Pages;

/// <summary>便笺列表页：搜索、筛选/排序图标行、列表与悬停操作按钮。</summary>
/// <remarks>
/// 展开结构：筛选/排序两个图标常显，点开才露出条件行；点条件再露出取值行
/// （颜色取值点击立即生效）。排序条件点击即加入排序链、再点取消。
/// </remarks>
public sealed partial class NoteListPage : UserControl
{
    private static readonly SolidColorBrush ChipSelectedBackground =
        new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush ChipSelectedBorder =
        new(Color.FromArgb(0x58, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush ChipBorder =
        new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush SwatchSelectedBorder =
        new(Color.FromArgb(255, 0x40, 0x37, 0x47));

    private static readonly SolidColorBrush TransparentBrush =
        new(Color.FromArgb(0, 0, 0, 0));

    private readonly ManagerViewModel _viewModel;

    public NoteListPage(ManagerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        UpdateChipStates();
    }

    /// <summary>XAML 的 x:Bind 从这里取值。</summary>
    public ManagerViewModel ViewModel => _viewModel;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        _viewModel.Query = SearchBox.Text;

    private void OnFilterIconClick(object sender, RoutedEventArgs e)
    {
        bool show = ColorFilterChip.Visibility == Visibility.Collapsed;
        ColorFilterChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 收起筛选组时把取值行一起收掉，免得留一行"孤儿"颜色。
        if (!show)
        {
            ColorFilterValues.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSortIconClick(object sender, RoutedEventArgs e) =>
        ModifiedTimeSortChip.Visibility = ModifiedTimeSortChip.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnColorChipClick(object sender, RoutedEventArgs e) =>
        ColorFilterValues.Visibility = ColorFilterValues.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

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
            button.BorderBrush = selected ? SwatchSelectedBorder : ChipBorder;
        }

        _viewModel.ColorFilter = clicked.Tag is string name && Enum.TryParse(name, out NoteColor color)
            ? color
            : null;

        UpdateChipStates();
    }

    private void OnModifiedTimeSortClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SortByModifiedTime = !_viewModel.SortByModifiedTime;
        UpdateChipStates();
    }

    private void OnNoteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NoteListItem item)
        {
            _viewModel.OpenNote(item.Note);
        }
    }

    private void OnItemPointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetItemActionsVisible((FrameworkElement)sender, visible: true);

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e) =>
        SetItemActionsVisible((FrameworkElement)sender, visible: false);

    private void OnItemColorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteListItem item ||
            sender is not Button button)
        {
            return;
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(10, 8, 10, 8),
        };

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };

        foreach (NoteColor color in Enum.GetValues<NoteColor>())
        {
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(NoteColorPalette.Accent(color)),
                BorderThickness = new Thickness(color == item.Note.Color ? 3 : 1),
                BorderBrush = color == item.Note.Color ? SwatchSelectedBorder : ChipBorder,
            };
            ToolTipService.SetToolTip(swatch, NoteColorPalette.DisplayName(color));

            NoteColor picked = color;
            swatch.Click += async (_, _) =>
            {
                flyout.Hide();
                await ApplyColorAsync(item, picked);
            };
            panel.Children.Add(swatch);
        }

        flyout.ShowAt(button);
    }

    private async void OnItemDuplicateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteListItem item)
        {
            return;
        }

        try
        {
            await _viewModel.DuplicateNoteAsync(item.Note);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("复制失败", exception.Message);
        }
    }

    private async void OnItemDeleteClick(object sender, RoutedEventArgs e)
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
                await ShowDialogAsync("未删除", "便签有修改尚未保存成功，已保留原地。请稍后再试。");
            }
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("删除失败", exception.Message);
        }
    }

    private async Task ApplyColorAsync(NoteListItem item, NoteColor color)
    {
        if (item.Note.Color == color)
        {
            return;
        }

        try
        {
            await _viewModel.ChangeColorAsync(item.Note, color);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("修改颜色失败", exception.Message);
        }
    }

    /// <summary>筛选/排序图标与排序条件 chip 的选中态（有筛选/排序生效时图标亮起）。</summary>
    private void UpdateChipStates()
    {
        SetChipSelected(FilterIconButton, _viewModel.ColorFilter is not null);
        SetChipSelected(SortIconButton, _viewModel.SortByModifiedTime);
        SetChipSelected(ModifiedTimeSortChip, _viewModel.SortByModifiedTime);
    }

    private static void SetChipSelected(Button button, bool selected)
    {
        button.Background = selected ? ChipSelectedBackground : TransparentBrush;
        button.BorderBrush = selected ? ChipSelectedBorder : TransparentBrush;
    }

    /// <summary>悬停时淡入/淡出右上角操作按钮；Opacity=0 仍可点击，命中要同步关掉。</summary>
    private static void SetItemActionsVisible(FrameworkElement itemRoot, bool visible)
    {
        if (itemRoot.FindName("ItemActions") is not StackPanel actions)
        {
            return;
        }

        actions.IsHitTestVisible = visible;

        var animation = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, actions);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

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

    private async Task ShowDialogAsync(string title, string content) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "确定",
        }.ShowAsync();
}
