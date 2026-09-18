using System.Windows;
using System.Windows.Input;
using LumiMemo.App.ViewModels;

namespace LumiMemo.App.Views;

/// <summary>
/// 管理器窗口（程序主界面，§15.8）。
/// </summary>
/// <remarks>
/// 代码后置只放<strong>纯交互</strong>：双击列表开一张便签。所有判断都在
/// <see cref="ManagerViewModel"/> 里，那样才测得到（§18.6）。
/// </remarks>
public partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;

    public ManagerWindow(ManagerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// 双击列表开一张便签。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="ManagerViewModel.SelectedNote"/> 而不是从事件源里挖 DataContext：
    /// 双击落在列表的空白处时不该做事，而那时 <c>SelectedNote</c> 是 <c>null</c>，
    /// 于是这个判断天然成立。从 <c>e.OriginalSource</c> 反查则要为
    /// 「点在 ScrollViewer 上」「点在 Border 上」各写一条分支。
    /// </remarks>
    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedNote is { } item)
        {
            _viewModel.OpenNoteCommand.Execute(item);
        }
    }
}
