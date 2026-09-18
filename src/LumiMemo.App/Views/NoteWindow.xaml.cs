using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LumiMemo.App.Services;
using LumiMemo.App.ViewModels;

namespace LumiMemo.App.Views;

/// <summary>
/// 单张便签的窗口（§15.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它有意做得很薄</strong>：除了「关窗前把内容存掉」这一件业务动作，
/// 其余全在 <see cref="NoteViewModel"/> 与 <c>WindowManager</c> 里。
/// 窗口层不该长业务判断——那些判断一旦落到这里就再也测不到了（§18.6）。
/// </para>
/// <para>
/// 位置与尺寸<strong>不在这里设置</strong>：由 <c>WindowManager</c> 在 <c>Show()</c>
/// 之前按 §13.8 的算法算好后写入。窗口自己不知道自己在哪，也不该知道。
/// </para>
/// </remarks>
public partial class NoteWindow : Window
{
    private readonly NoteViewModel _viewModel;
    private readonly AutoSaveService _autoSaveService;

    /// <summary>是否已经排过一轮「存完再关」。防止用户连点关闭按钮排出一串。</summary>
    private bool _isClosePending;

    /// <summary>内容已经存好，这次 <c>Closing</c> 放行。</summary>
    /// <remarks>
    /// 没有它就会死循环：保存完了再调 <see cref="Window.Close"/>，
    /// 又会进一次 <c>OnClosing</c>，又取消一次关闭，又存一次……
    /// </remarks>
    private bool _canClose;

    public NoteWindow(NoteViewModel viewModel, AutoSaveService autoSaveService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(autoSaveService);

        InitializeComponent();

        _viewModel = viewModel;
        _autoSaveService = autoSaveService;

        // 初始的置顶状态直接落到窗口上。之后的变化由 WindowManager 订阅
        // ViewModel 的属性变更来同步——窗口这边不参与那条链路。
        Topmost = viewModel.IsTopMost;

        DataContext = viewModel;
    }

    /// <summary>本窗口所属的便签 id。<c>WindowManager</c> 用它维护映射。</summary>
    public Guid NoteId => _viewModel.Id;

    /// <summary>本窗口绑定的 ViewModel。<c>WindowManager</c> 用它订阅属性变更。</summary>
    public NoteViewModel ViewModel => _viewModel;

    /// <summary>
    /// 标题栏拖动（§15.2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 直接 <see cref="Window.DragMove"/> 是最省事的做法，但它有两个已知缺口，
    /// 都留给后续阶段补：
    /// </para>
    /// <list type="bullet">
    ///   <item>没有 Aero Snap（拖到屏幕边缘吸附）与双击最大化。要拿回这两样，
    ///   得在 <c>WM_NCHITTEST</c> 里对被判定为「标题栏」的区域返回 <c>HTCAPTION</c>，
    ///   而不是把命中测试让给 WPF。本轮没有 Win32 消息钩子，暂缺。</item>
    ///   <item>锁定态的便签不该能拖动。本轮还没有锁定按钮，等它出现时在这里加判断。</item>
    /// </list>
    /// </remarks>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            // 无边框便签没有最大化的语义，双击什么都不做，免得误触后窗口铺满整屏。
            return;
        }

        DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 关窗前把还没落盘的内容存掉（§17.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 去抖是 500 毫秒，用户打完最后一个字立刻关窗口是常规操作，
    /// 不管的话最近那一下编辑就丢了。所以这里必须<strong>同步等到写完再关</strong>。
    /// </para>
    /// <para>
    /// 做法是取消这次关闭、异步等待、再关一次——<c>Closing</c> 是同步事件签不出 await，
    /// 而阻塞 UI 线程去等一个续体要回 UI 线程的 <c>Task</c> 会直接死锁。
    /// 取消再关是这条约束下唯一不锁死的写法。
    /// </para>
    /// <para>
    /// <strong>第二次 <c>Close()</c> 必须排进消息队列，不能直接调。</strong>
    /// 若那次保存恰好同步完成（内存缓存命中、或者写盘快到没有真正让出线程），
    /// <c>await</c> 之后的续体会在 <c>OnClosing</c> 的调用栈里继续跑，
    /// 于是变成「在 <c>Closing</c> 处理中再调一次 <c>Close()</c>」——
    /// 嵌套关闭是 WPF 没定义的用法。排一次队就彻底绕开它。
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_canClose)
        {
            return;
        }

        e.Cancel = true;

        if (_isClosePending)
        {
            return;
        }

        _isClosePending = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(async () =>
            {
                // 这是整个窗口生命周期里唯一必须落盘的时刻，走 SaveNowAsync 绕过去抖。
                // 它内部的异常已经吞掉了（§11.5），这里不需要再包一层 try。
                await _autoSaveService.SaveNowAsync(_viewModel.Id);

                _canClose = true;
                Close();
            }));
    }
}
