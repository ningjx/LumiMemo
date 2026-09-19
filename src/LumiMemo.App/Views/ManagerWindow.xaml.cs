using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LumiMemo.App.Services;
using LumiMemo.App.ViewModels;

namespace LumiMemo.App.Views;

/// <summary>
/// 管理器窗口（程序主界面，§15.8）。
/// </summary>
/// <remarks>
/// 代码后置只放<strong>纯交互</strong>：双击列表开一张便签，齿轮按钮开设置窗口。
/// 所有判断都在 <see cref="ManagerViewModel"/> 里，那样才测得到（§18.6）。
/// </remarks>
public partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly SettingsWindowLauncher _settingsLauncher;

    public ManagerWindow(ManagerViewModel viewModel, SettingsWindowLauncher settingsLauncher)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsLauncher);

        InitializeComponent();

        _viewModel = viewModel;
        _settingsLauncher = settingsLauncher;
        DataContext = viewModel;
    }

    /// <summary>
    /// 双击某一行开一张便签。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>「左键」与「落在某一行上」两个条件缺一不可</strong>，少了哪个都会出事：
    /// </para>
    /// <para>
    /// <strong>① 判左右键。</strong> <c>Control.MouseDoubleClick</c> 挂在
    /// <c>Mouse.MouseDown</c> 上、只看 <c>ClickCount == 2</c>，<strong>左右键都会触发</strong>。
    /// 少了这一条，用户在列表上右键连点两下（想调出菜单、或者只是手快）就会把
    /// 上一轮选中的那张便签打开——表现出来是「右键一下，凭空冒出一张便签窗口，
    /// 管理器还失了焦」。
    /// </para>
    /// <para>
    /// <strong>② 判落在哪一行。</strong> 早先这里只有 <c>SelectedNote</c> 非空这一个条件，
    /// 理由是「双击空白处时它天然是 <c>null</c>」——<strong>这个前提是错的</strong>：
    /// <c>ListBox</c> 点空白处<strong>不会</strong>清空 <c>SelectedItem</c>，
    /// 于是左键双击空白处照样会把上一次选中的那张打开。
    /// 用 <c>ContainerFromElement</c> 反查命中的容器，空白处给不出容器，判断才有意义。
    /// </para>
    /// </remarks>
    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source
            || NoteList.ContainerFromElement(source) is not ListBoxItem)
        {
            return;
        }

        if (_viewModel.SelectedNote is { } item)
        {
            _viewModel.OpenNoteCommand.Execute(item);
        }
    }

    /// <summary>
    /// 右键落在哪一行就先选中哪一行；落在空白处则整个菜单不弹。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 菜单项作用在 <see cref="ManagerViewModel.SelectedNote"/> 上，而 WPF 的
    /// <c>ListBox</c> <strong>不会</strong>因为右键而改变选中项。少了这一步，
    /// 用户在一个没选中的行上右键点「移入回收站」，删掉的是他上一次选的那张——
    /// 而且删完列表一刷新，他连「刚才删的是谁」都无从对照。
    /// </para>
    /// <para>
    /// 落在空白处（或滚动条上）时 <c>ContainerFromElement</c> 给不出容器，
    /// 这时同样不能放行菜单：那一个「移入回收站」会对着一个与鼠标位置无关的选中行执行。
    /// 憋回去比给一个会误伤的菜单好。
    /// </para>
    /// </remarks>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && NoteList.ContainerFromElement(source) is ListBoxItem item)
        {
            item.IsSelected = true;

            return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// 开设置窗口（§15.9）。
    /// </summary>
    /// <remarks>
    /// 没走 ViewModel 的命令，因为它<strong>不涉及任何状态判断</strong>——
    /// 「已开着就唤到前面」这条规则在 <see cref="SettingsWindowLauncher"/> 里，
    /// 放在这里只是转发。真有一个「什么时候不该开」的条件时再挪进 ViewModel。
    /// </remarks>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // 返回值（是否新开了窗口）在界面上没有用武之地，管理器窗口不需要知道。
        _ = _settingsLauncher.Show();
    }
}
