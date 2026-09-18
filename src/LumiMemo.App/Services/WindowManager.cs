using System.Windows;
using System.Windows.Interop;
using LumiMemo.App.Abstractions;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Infrastructure.Windows;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IWindowManager"/> 的生产实现（§14.3）：持有 <c>Guid → NoteWindow</c> 的映射，
/// 并且是<strong>物理像素与 DIP 之间唯一的换算点</strong>（§14.4）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>换算方向只有两条，都在这里，别处不许再有：</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     放置（物理 → DIP）：<see cref="LayoutMath.Restore"/> 算出来的是物理像素，
///     而 WPF 的 <c>Left/Top/Width/Height</c> 是 DIP，除一下缩放系数即可。
///   </item>
///   <item>
///     回写（DIP → 物理）：用 <c>RestoreBounds</c> 乘 <c>TransformToDevice.M11</c>，
///     DPI 由 <c>96 × M11</c> 反推。
///   </item>
/// </list>
/// <para>
/// 之所以只用 WPF 的 <c>Left/Top</c> 而<strong>不用 <c>SetWindowPos</c></strong>：
/// 后者需要 Win32 互操作（本项目把它全部关在 Infrastructure 里，见决策 3），
/// 而单显示器、单 DPI 下 WPF 的换算结果与它完全一致。
/// <strong>已知局限</strong>：混合 DPI 的多显示器环境里，WPF 的坐标是相对主显示器
/// 且按各屏缩放折算过的，跨屏摆位会有偏差。等 Win32 那一层接上后换成 <c>SetWindowPos</c>，
/// 本类的这两处换算随之删掉。
/// </para>
/// <para>
/// <strong>它不做业务判断</strong>：不创建便签、不删除便签。「该不该有窗口」是
/// <c>INoteService</c> 的事（§14.1）。它只在窗口真的关掉之后，回头通知一句
/// <see cref="INoteService.MarkNoteClosed"/>。
/// </para>
/// </remarks>
public sealed class WindowManager : IWindowManager, IDisposable
{
    private readonly LayoutService _layout;
    private readonly INoteService _noteService;
    private readonly AutoSaveService _autoSaveService;
    private readonly IDisplayProvider _displays;
    private readonly ILogger<WindowManager> _logger;
    private readonly ShellForegroundWatcher _foreground = new();

    private readonly Dictionary<Guid, NoteWindow> _windows = [];

    /// <summary>
    /// 因为「显示桌面」而被临时提到置顶档的窗口句柄。
    /// </summary>
    /// <remarks>
    /// 只撤退我们提升过的那些。没有这份记录就只能靠 <c>WS_EX_TOPMOST</c> 反查，
    /// 那会把用户**主动置顶**的便签一起降下来。
    /// </remarks>
    private readonly HashSet<IntPtr> _promotedForShowDesktop = [];

    /// <summary>
    /// 本次会话中已经层叠了几张。
    /// </summary>
    /// <remarks>
    /// 只在「保存时那台显示器已经不在了」这条路径上用到（§13.8）。判定方式是
    /// 「算出来的目标显示器与保存记录里的不是同一台」——这是个近似：用户把窗口
    /// 从 A 屏拖到 B 屏之后保存，两边是一致的，所以不会误判；而真的拔掉显示器时
    /// 保存记录指向一台不存在的设备，必然对不上。
    /// </remarks>
    private int _cascadeIndex;

    /// <summary>
    /// 「显示桌面」（Win+D）之后，是否让不置顶的便签仍然留在桌面上（§13.6）。
    /// 由 <c>StartupSequence</c> 从设置推入，默认开。
    /// </summary>
    /// <remarks>
    /// 关掉它，不置顶的便签就和普通窗口一样被升起的桌面盖住；置顶那一档不受影响，
    /// 系统本来就不动置顶窗口。
    /// </remarks>
    public bool RestoreAfterShowDesktop { get; set; } = true;

    public WindowManager(
        LayoutService layout,
        INoteService noteService,
        AutoSaveService autoSaveService,
        IDisplayProvider displays,
        ILogger<WindowManager> logger)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(autoSaveService);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(logger);

        _layout = layout;
        _noteService = noteService;
        _autoSaveService = autoSaveService;
        _displays = displays;
        _logger = logger;

        // 构造发生在 UI 线程（组合根在 App.OnStartup 里同步解析），这一点是硬要求：
        // WINEVENT_OUTOFCONTEXT 的回调投递到注册线程的消息队列上，在别的线程装钩子等于装了个哑巴。
        _foreground.ForegroundChanged += OnForegroundChanged;

        if (!_foreground.Start())
        {
            // 装不上不是致命错误：唯一的后果是「显示桌面」后不置顶的便签会被盖住，
            // 也就是退回到普通窗口的行为。但这必须留痕，否则就成了功能静默失效。
            _logger.LogWarning("前台事件钩子安装失败，「显示桌面」后便签将保持不住。");
        }
    }

    /// <inheritdoc />
    public void ShowNote(NoteViewModel viewModel, NoteLayout layout)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(layout);

        if (_windows.TryGetValue(viewModel.Id, out NoteWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();

            return;
        }

        var window = new NoteWindow(viewModel, _autoSaveService);

        ApplyPlacement(window, layout);
        HookWindow(window, viewModel);

        _windows[viewModel.Id] = window;

        window.Show();

        // 首个位置要立刻记进 layout：用户开了一张全新便签之后直接退出，
        // 若此时 layout 里还是默认值，下次启动它就会跳回默认位置。
        CaptureGeometry(viewModel.Id, layout);
        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <inheritdoc />
    public void CloseNote(Guid noteId)
    {
        if (_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            // 真正从字典里移除发生在 Closed 之后（见 OnNoteWindowClosed），
            // 因为关闭可能被 OnClosing 里的保存取消。
            window.Close();
        }
    }

    /// <inheritdoc />
    public void HideAllNotes()
    {
        foreach (NoteWindow window in _windows.Values)
        {
            window.Hide();
        }
    }

    /// <inheritdoc />
    public void ShowAllNotes()
    {
        bool isFirst = true;

        foreach (NoteWindow window in _windows.Values)
        {
            window.Show();

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (isFirst)
            {
                window.Activate();
                isFirst = false;
            }
        }
    }

    /// <summary>
    /// 把窗口当前的几何信息写回 <paramref name="target"/>（物理像素 + DPI，§14.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 <see cref="Window.RestoreBounds"/> 而不是 <c>Left</c>/<c>Width</c>：
    /// 窗口被最大化或最小化时那两个属性给的是当前状态的值，而我们要记的永远是
    /// 「还原后」的尺寸——用户把便签最大化看一眼再关掉，下次不该开成最大化。
    /// </para>
    /// <para>
    /// <strong>拿不到 <c>PresentationSource</c> 时必须原样返回，绝不能写 0。</strong>
    /// 它在 <c>Show()</c> 之前、窗口还没创建句柄时是 <c>null</c>；
    /// 而一次写 0 就会把用户摆好的布局永久清成 (0,0)——这类错误无法从界面察觉得到，
    /// 只会在下次启动时表现为「便签全跑到左上角」。
    /// </para>
    /// </remarks>
    public void CaptureGeometry(Guid noteId, NoteLayout target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        PresentationSource? source = PresentationSource.FromVisual(window);
        double m11 = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;

        if (m11 <= 0)
        {
            return;
        }

        Rect bounds = window.RestoreBounds;

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        target.X = bounds.X * m11;
        target.Y = bounds.Y * m11;
        target.Width = bounds.Width * m11;
        target.Height = bounds.Height * m11;
        target.Dpi = (uint)System.Math.Round(96 * m11);

        // 折叠态那 44 DIP 不是用户拖出来的高度，写进 ExpandedHeight 会让
        // 「展开」变成展开到 44 DIP，窗口再也长不回来。
        if (!window.ViewModel.IsCollapsed)
        {
            target.ExpandedHeight = target.Height;
        }

        DisplaySnapshot? display = _displays.FindDisplayContaining(
            target.X + (target.Width / 2),
            target.Y + (target.Height / 2));

        target.DisplayId = display?.DeviceId;
    }

    /// <inheritdoc />
    public void ApplyCollapsed(Guid noteId, bool collapsed)
    {
        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        NoteLayout layout = window.ViewModel.Layout;
        double scale = DeviceScale(window);

        if (collapsed)
        {
            // 折叠前先把展开高度记下来，否则展开时无处可查。
            if (!layout.IsCollapsed)
            {
                layout.ExpandedHeight = window.Height * scale;
            }

            window.Height = LayoutMath.CollapsedHeight(_layout.ShowStatusBar);
        }
        else
        {
            window.Height = layout.ExpandedHeight / scale;
        }

        // 折叠状态存在 NoteLayout 里（§9.2），由这里写回，ViewModel 只负责发通知。
        layout.IsCollapsed = collapsed;

        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// 切换置顶。
    /// </summary>
    /// <param name="noteId">目标便签。</param>
    /// <param name="topMost">是否置顶。</param>
    /// <remarks>
    /// <strong>这是用户自己设的那一档</strong>，与「显示桌面」期间的临时置顶（见
    /// <see cref="PromoteForShowDesktop"/>）是两回事：置顶的便签永远跳过临时提升与撤退，
    /// 所以这里的设置不会被那套机制改回去。
    /// <strong>不要试图改用 owner / 桌面层来豁免</strong>：那条路实测会让窗口整片渲染成黑色，
    /// 且并没有真的设上 owner（附录 D.7）。
    /// </remarks>
    public void ApplyTopMost(Guid noteId, bool topMost)
    {
        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        window.Topmost = topMost;
        window.ViewModel.Layout.IsTopMost = topMost;

        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <inheritdoc />
    public void ApplyLocked(Guid noteId, bool locked)
    {
        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        window.ViewModel.Layout.IsLocked = locked;

        // 窗口层暂时只有「正文不可编辑」这一个可见效果。
        // 真正的「不可拖动」要在 WM_NCHITTEST 里对被锁定的窗口返回 HTCLIENT，
        // 那需要 Win32 消息钩子，本轮还没有（§13.7）。
        // 不要在 XAML 里绑 TextBox.IsReadOnly ——绑了也只在窗口重建时才生效。
        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <summary>点击穿透（§13.7）。</summary>
    /// <remarks>
    /// 一律返回 <see langword="false"/>：这个能力的原型在文档里就没跑通，
    /// 而它依赖的 <c>WS_EX_TRANSPARENT</c> + 分层窗口同样要走 Win32 那一层。
    /// 返回 false 而不是抛异常，是因为调用方（将来的锁定功能）据此走降级路径，
    /// 它属于「暂时做不到」，不是「出错了」。
    /// </remarks>
    public bool TryApplyClickThrough(Guid noteId, bool enabled) => false;

    /// <summary>
    /// 显示器配置变了（<c>WM_DISPLAYCHANGE</c>）：所有窗口按新配置重算一遍位置。
    /// </summary>
    /// <remarks>
    /// 位置全部重算而不是只处理受影响的那些：判断「哪些窗口受影响」需要拿到
    /// 变化前后的显示器集合做差，而重算一次的代价只是几次浮点运算。
    /// </remarks>
    public void OnDisplayConfigurationChanged()
    {
        foreach (NoteWindow window in _windows.Values)
        {
            ApplyPlacement(window, window.ViewModel.Layout);
            CaptureGeometry(window.NoteId, window.ViewModel.Layout);
        }

        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <inheritdoc />
    public bool IsNoteOpen(Guid noteId) => _windows.ContainsKey(noteId);

    /// <summary>
    /// 按 §13.8 算好位置并写到窗口上。物理像素 → DIP 的换算就在这一行除法里。
    /// </summary>
    private void ApplyPlacement(NoteWindow window, NoteLayout layout)
    {
        WindowPlacement placement = _layout.ResolvePlacement(layout, _cascadeIndex);

        if (!string.Equals(placement.DisplayId, layout.DisplayId, StringComparison.Ordinal))
        {
            // 保存时的显示器不在了，这张是被层叠重排的，给它占一个层叠位。
            _cascadeIndex++;
        }

        double scale = placement.Dpi / 96.0;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Bounds.X / scale;
        window.Top = placement.Bounds.Y / scale;
        window.Width = placement.Bounds.Width / scale;
        window.Height = placement.Bounds.Height / scale;
    }

    private void HookWindow(NoteWindow window, NoteViewModel viewModel)
    {
        window.Closed += (_, _) => OnNoteWindowClosed(viewModel.Id);

        // 移动与缩放都回写几何。这两个事件在拖动期间会连续触发几十次，
        // 而 MarkDirtyAndScheduleFlush 是节流的，所以磁盘最多一秒被碰一次。
        //
        // §8.5 要求「拖动期间绝不写盘」的严格做法是在 WM_EXITSIZEMOVE 里写一次，
        // 那要 Win32 消息钩子。本轮用节流近似：多写的几次是普通文件写，
        // 代价远小于为此提前引入一整套消息处理（见「已知局限」）。
        window.LocationChanged += (_, _) => OnGeometryChanged(viewModel.Id);
        window.SizeChanged += (_, _) => OnGeometryChanged(viewModel.Id);

        viewModel.PropertyChanged += (_, e) => OnViewModelPropertyChanged(viewModel.Id, e.PropertyName);
    }

    /// <summary>
    /// 前台窗口换了：桌面升上来就把不置顶的便签临时提到置顶档，桌面下去就撤回来。
    /// </summary>
    /// <param name="foreground">新的前台窗口句柄。</param>
    /// <remarks>
    /// <para>
    /// <strong>为什么便签需要这一套。</strong>「显示桌面」并不最小化便签——便签窗口
    /// <c>ShowInTaskbar="False"</c>，WPF 因此给它挂了个 Hidden Window 当 owner，
    /// 而桌面**跳过一切有 owner 的窗口**（实测：按完 Win+D，管理器的
    /// <c>IsIconic</c> 为真，三张便签全为假且 <c>IsWindowVisible</c> 全程为真）。
    /// 用户看到的「便签没了」其实是被升起来的桌面窗口**盖住**了——点回任意窗口
    /// 桌面就降下去，便签原样露出来。
    /// </para>
    /// <para>
    /// <strong>普通 z 序那一档够不到桌面之上。</strong>实测三条路全部落空：
    /// 事后 <c>SetWindowPos(HWND_TOP, SWP_NOACTIVATE)</c> 压不过；
    /// 挪到「桌面刚成为前台」的那一刻调用，只在头两秒有效、之后又被盖回；
    /// 而唯一看起来成功的那次是把前台抢回了便签（<c>focusStolen=YES</c>），
    /// 用户正站在桌面上，抢焦点意味着接下来的按键会打进便签里。
    /// 只有置顶档（<c>WS_EX_TOPMOST</c>）压得住。
    /// </para>
    /// <para>
    /// <strong>撤退时为什么不能一律把便签排到新前台之后。</strong>「显示桌面」结束
    /// 不是一瞬间的事：系统先把被它掀掉的那批窗口逐个还原（管理器就在其中），
    /// 之后才把前台还给原来那个窗口。还原过程中我们会先收到一次
    /// 「前台 = 管理器」——如果据此就把便签插到管理器后面，等系统把前台还给便签时，
    /// 便签就成了一张<em>名义上是前台、实际被盖住</em>的窗口（实测复现）。
    /// 判据是「新前台是不是本进程的窗口」：是，说明这是系统在还原我们自己的窗口，
    /// 不是用户点了别处，便签只清标志、不动 z 序；否，才是用户真的切走了。
    /// </para>
    /// <para>
    /// 这就是 <see cref="RestoreAfterShowDesktop"/> 的落点：关掉它，本方法直接撤退，
    /// 便签与普通窗口无异。
    /// </para>
    /// </remarks>
    private void OnForegroundChanged(IntPtr foreground)
    {
        if (!RestoreAfterShowDesktop)
        {
            ReleaseTemporaryTopMost(IntPtr.Zero);

            return;
        }

        if (foreground == WindowInterop.GetShellWindowHandle())
        {
            PromoteForShowDesktop();

            return;
        }

        ReleaseTemporaryTopMost(
            WindowInterop.BelongsToCurrentProcess(foreground) ? IntPtr.Zero : foreground);
    }

    /// <summary>把所有可见的、非置顶的便签临时提到置顶档。</summary>
    /// <remarks>
    /// 用户主动置顶的便签跳过：它们本来就在置顶档，再动一次只会在撤退时多一份
    /// "这张是不是我们提的" 的歧义。
    /// </remarks>
    private void PromoteForShowDesktop()
    {
        foreach (NoteWindow window in _windows.Values)
        {
            if (!window.IsVisible || window.ViewModel.Layout.IsTopMost)
            {
                continue;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;

            if (WindowInterop.MakeTopMost(hwnd))
            {
                _promotedForShowDesktop.Add(hwnd);
            }
        }
    }

    /// <summary>撤回 <see cref="PromoteForShowDesktop"/> 提升过的窗口。</summary>
    /// <param name="foreground">
    /// 用户刚切过去的窗口，用来把便签排到它后面；<see cref="IntPtr.Zero"/> 表示不动 z 序。
    /// </param>
    private void ReleaseTemporaryTopMost(IntPtr foreground)
    {
        if (_promotedForShowDesktop.Count == 0)
        {
            return;
        }

        foreach (IntPtr hwnd in _promotedForShowDesktop)
        {
            WindowInterop.ClearTopMost(hwnd, foreground);
        }

        _promotedForShowDesktop.Clear();
    }

    private void OnGeometryChanged(Guid noteId)
    {
        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        CaptureGeometry(noteId, window.ViewModel.Layout);
        _layout.MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// ViewModel 上的状态变化同步到窗口。
    /// </summary>
    /// <remarks>
    /// <strong>为什么不让 XAML 直接双向绑定</strong>：<c>Window.Topmost</c> 这类属性一旦被
    /// 代码写入就会把绑定整个清掉（WPF 的依赖属性语义），而 <c>WindowManager</c> 的
    /// 公开方法（<see cref="ApplyTopMost"/> 等）仍然要能直接设置它们。走这一个入口，
    /// 两个方向的写入才是同一份逻辑，不会互相打架。
    /// </remarks>
    private void OnViewModelPropertyChanged(Guid noteId, string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(NoteViewModel.IsTopMost):
                ApplyTopMost(noteId, GetViewModel(noteId)?.IsTopMost ?? false);
                break;

            case nameof(NoteViewModel.IsCollapsed):
                ApplyCollapsed(noteId, GetViewModel(noteId)?.IsCollapsed ?? false);
                break;

            case nameof(NoteViewModel.IsLocked):
                ApplyLocked(noteId, GetViewModel(noteId)?.IsLocked ?? false);
                break;

            default:
                // 正文、标题、字数、保存状态全是纯数据绑定，窗口这边不需要动作。
                break;
        }
    }

    private NoteViewModel? GetViewModel(Guid noteId) =>
        _windows.TryGetValue(noteId, out NoteWindow? window) ? window.ViewModel : null;

    /// <summary>
    /// 窗口真的关掉了：解除映射，并告诉业务层这张便签的窗口已经不在。
    /// </summary>
    /// <remarks>
    /// 放在 <c>Closed</c> 而不是 <c>Closing</c> 里，是因为 <c>Closing</c> 可能被
    /// <see cref="NoteWindow"/> 里那次「存完再关」取消掉。在 <c>Closing</c> 里
    /// 就置 <c>IsOpen = false</c> 的话，一次被取消的关闭会让 layout 记成「已关闭」，
    /// 而窗口其实还开着。
    /// </remarks>
    private void OnNoteWindowClosed(Guid noteId)
    {
        if (!_windows.Remove(noteId, out NoteWindow? window))
        {
            return;
        }

        // 句柄会随窗口一起失效，留在提升记录里的话，下次撤退时就是在往一个
        // 已经销毁的句柄上调 SetWindowPos。句柄还可能被系统复用。
        _promotedForShowDesktop.Remove(new WindowInteropHelper(window).Handle);

        _noteService.MarkNoteClosed(noteId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _foreground.ForegroundChanged -= OnForegroundChanged;
        _foreground.Dispose();
        _promotedForShowDesktop.Clear();
    }

    /// <summary>窗口当前的 DIP → 物理像素缩放系数（1.0 = 100%，1.5 = 150%）。</summary>
    private static double DeviceScale(Window window)
    {
        PresentationSource? source = PresentationSource.FromVisual(window);
        double m11 = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;

        return m11 > 0 ? m11 : 1.0;
    }
}
