using System.Windows;
using LumiMemo.App.ViewModels;

namespace LumiMemo.App.Views;

/// <summary>
/// 设置窗口（§15.9）。回收站是它的一个页签，没有独立窗口。
/// </summary>
/// <remarks>
/// <para>
/// 代码后置只放两件窗口非做不可的事：<strong>打开时把设置读进来</strong>
/// （构造函数不能 <c>await</c>），以及<strong>把 ViewModel 的关闭请求接到
/// <see cref="Window.Close"/></strong>。所有判断都在
/// <see cref="SettingsViewModel"/> 里，那样才测得到（§18.6）。
/// </para>
/// <para>
/// <strong>每次打开都是一个新窗口</strong>（<c>SettingsWindowLauncher</c> 保证同时只有一个），
/// 所以「读设置」这件事<strong>每开一次都要重来</strong>，挂在 <c>Loaded</c> 上而不是构造函数里——
/// 构造函数不能 <c>await</c>，而读设置要碰磁盘。
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // ViewModel 不知道窗口的存在（§18.3），它只发一个请求，关闭由这里执行。
        _viewModel.CloseRequested += OnCloseRequested;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 把窗口切到指定页签。
    /// </summary>
    /// <remarks>
    /// 托盘菜单的「回收站（N）...」要求落在回收站那一页上（§15.9）。窗口已经开着时
    /// <c>SettingsWindowLauncher</c> 会走这里切页，而不是关掉重开——重开会让用户
    /// 在另一页上刚填了一半的东西凭空消失。
    /// </remarks>
    public void SelectTab(SettingsTab tab) => Tabs.SelectedIndex = (int)tab;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 只读一次：Loaded 在窗口生命期内可能再被触发（比如换了父级或重新挂到视觉树上）。
        Loaded -= OnLoaded;

        // 读设置要把回收站列表也刷一遍（那是磁盘 IO），不能阻塞界面。
        // 命令内部已经把结果封送回 UI 线程，这里直接发出去即可（§3.4 规则 T6）。
        _viewModel.LoadCommand.Execute(null);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;

        base.OnClosed(e);
    }
}
