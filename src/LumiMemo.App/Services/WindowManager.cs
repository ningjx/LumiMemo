using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

    /// <summary>进程正在退出：此后关掉的窗口都不算「用户关掉了这张便签」。见 <see cref="BeginShutdown"/>。</summary>
    private bool _isShuttingDown;

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
    /// 每张被临时提升的便签<strong>提升之前</strong>的那个 z 序邻居，也就是当时盖住它的窗口。
    /// </summary>
    /// <remarks>
    /// 撤退时靠它"从哪儿来回哪儿去"。<strong>不拿"撤退那一刻的前台窗口"当锚点</strong>：
    /// 两条入口的前台序列不一样（见 <see cref="OnForegroundChanged"/>），Win+D 会把前台
    /// 还给原来那个窗口，而点任务栏按钮时前台停在任务栏上——那是个置顶窗口，拿它当锚点
    /// 会把便签一并提拔进置顶档。
    /// </remarks>
    private readonly Dictionary<IntPtr, IntPtr> _predecessorBeforePromotion = [];

    /// <summary>
    /// 每张便签在<strong>最近一次「桌面没有升起来」的时候</strong>的 z 序邻居。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PromoteForShowDesktop"/> 的锚点取自这里，<strong>而不是当场去问一次
    /// <see cref="WindowInterop.GetZOrderPredecessor"/></strong>。
    /// </para>
    /// <para>
    /// <strong>当场问一定是错的，只是错得时好时坏。</strong>提升发生在「前台已经变成桌面」
    /// 之后，而那一刻「显示桌面」<em>已经把盖住便签的那批普通窗口藏起来了</em>——
    /// 于是往上走时它们全被判成"不可见窗口"跳过，问到的要么是别的便签窗口，要么直接
    /// 走到顶端拿到 <see cref="IntPtr.Zero"/>。撤退时插到这样一个锚点后面，便签自然回不到原位：
    /// 用户看到的就是"便签跑到最底下，再按显示桌面就跟着别的窗口一起消失/恢复"。
    /// </para>
    /// <para>
    /// <strong>而这个"藏起来"与"我们的回调"是个赛跑</strong>，所以症状是时好时坏——
    /// 藏得慢的那几次，当场问恰好还能问到对的窗口，看着就像修好了。这也正是它被误判成
    /// "跟谁启动有关"的原因：同一个实例往往连着复现同一种结果。在这里持续采样，
    /// 结果就与那一瞬间的先后顺序无关了。
    /// </para>
    /// <para>
    /// 采样点见 <see cref="RememberPredecessors"/>。只在"正常桌面"下采：
    /// 便签正被我们临时提着的时候问到的邻居是置顶区的，没有任何意义。
    /// </para>
    /// </remarks>
    private readonly Dictionary<IntPtr, IntPtr> _lastKnownPredecessor = [];

    /// <summary>
    /// 已经退出置顶档、但<strong>位置还没落定</strong>的便签：句柄 → 提升前的那个 z 序邻居。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 归位不是一次 <c>SetWindowPos</c> 就能了事的。<see cref="ReleaseTemporaryTopMost"/> 由
    /// 「前台不再是桌面」触发，而那一刻「显示桌面」<em>还在把窗口一张张恢复上来</em>——
    /// 每恢复一张就把它抬到顶上，于是刚插好的位置立刻被顶下去。实测轨迹：还原后 +45ms
    /// 便签已经落在正确的邻居后面，+91ms 就掉到最底下，此后纹丝不动。
    /// </para>
    /// <para>
    /// 所以归位得<strong>盯着做</strong>：见 <see cref="SettleRestoredWindows"/>，隔一小段复查一次，
    /// 谁的邻居还不是锚点就再插一次，一直管到窗口期跑完——中途"这会儿是对的"并不算数，
    /// 恢复洪流可能还在后头（实测点任务栏按钮时就是这样：+1ms 已就位，+150ms 又被顶下去）。
    /// </para>
    /// </remarks>
    private readonly Dictionary<IntPtr, IntPtr> _restorePending = [];

    /// <summary>归位复查定时器，第一次需要复查时才建（见 <see cref="EnsureRestoreTimer"/>）。</summary>
    private DispatcherTimer? _restoreTimer;

    /// <summary>本轮归位复查已经跑了几拍。</summary>
    private int _restoreTicks;

    /// <summary>归位复查的间隔。够短，看不出便签在动。</summary>
    private static readonly TimeSpan RestoreSettleInterval = TimeSpan.FromMilliseconds(60);

    /// <summary>归位复查最多跑多少拍，约合 1 秒——「显示桌面」的恢复早已结束。</summary>
    private const int RestoreSettleMaxTicks = 16;

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

        // 新开的窗口此刻的邻居就是它的原位。此时不记，用户开完便签直接按 Win+D，
        // 这张便签就没有任何锚点可用。
        RememberPredecessors();
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

    /// <inheritdoc />
    public void BeginShutdown() => _isShuttingDown = true;

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
    /// <strong>撤退时插回的锚点记在提升之前，与当前前台无关。</strong>这里只判断一件事：
    /// 新前台是不是桌面窗口。是就提升，不是就撤退——<em>不去分辨</em>前台是本进程的窗口
    /// 还是别人的窗口，也不拿它当撤退的锚点。
    /// </para>
    /// <para>
    /// 两个原因，都实测踩过。其一，「显示桌面」有<strong>两条入口</strong>：按 Win+D 时
    /// 前台直接变成 <c>Progman</c>；点任务栏右下角那个按钮时，前台先在<strong>置顶档的</strong>
    /// <c>Shell_TrayWnd</c> 上停 60～110ms（Win+D 只要 15～30ms）才落到 <c>Progman</c>，
    /// 而<em>还原</em>时它压根不去别处，就停在任务栏上。其二，Win+D 还原时系统会先把被掀掉的
    /// 那批窗口逐个还原（管理器就在其中），之后才把前台还给原来那个窗口，于是"撤退那一刻的
    /// 前台"是一张一直在变的牌。拿这样一张牌当锚点，要么把便签插到一个置顶窗口后面而被
    /// 一并提拔（见 <see cref="WindowInterop.PlaceBehind"/>），要么让它落进「名义上是前台、
    /// 实际被盖住」的非自然位置（实测复现）。
    /// </para>
    /// <para>
    /// 所以锚点取自 <see cref="_predecessorBeforePromotion"/>——提升<em>之前</em>就记下的
    /// 那个邻居。便签于是回到原本的层级：本来被某个窗口盖着，还原后照样被它盖着。
    /// </para>
    /// <para>
    /// <strong>而"提升之前"必须早到桌面还没升起来的时候。</strong>本方法收到桌面成为前台
    /// 的通知时，「显示桌面」已经把盖住便签的窗口藏好了，那一刻去问"上面是谁"只会问到
    /// 别的便签窗口或 <see cref="IntPtr.Zero"/>。<see cref="RememberPredecessors"/> 因此
    /// 在每次<em>正常</em>的前台切换之后顺手采一遍（见 <see cref="_lastKnownPredecessor"/>），
    /// 提升时直接用采到的值。
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
            ReleaseTemporaryTopMost();

            return;
        }

        if (foreground == WindowInterop.GetShellWindowHandle())
        {
            // 桌面又升起来了：上一轮的归位复查到此为止——便签马上要重新进置顶档，
            // 这时再去把它们往普通档里插，是在和 PromoteForShowDesktop 对着干。
            CancelRestoreSettle();
            PromoteForShowDesktop();

            return;
        }

        // 顺序不能颠倒：先把临时置顶撤干净（此时便签已经回到普通档），
        // 采到的才是它们在正常 z 序里的位置。反过来的话，问到的是置顶区的邻居。
        ReleaseTemporaryTopMost();

        // 刚释放过的话，这个采样点正落在「显示桌面」的恢复洪流里，采到的邻居不可信。
        // 交给 SettleRestoredWindows 在落定之后再采。
        if (_restorePending.Count == 0)
        {
            RememberPredecessors();
        }
    }

    /// <summary>
    /// 记下每张可见便签此刻盖着它的那个窗口，供 <see cref="PromoteForShowDesktop"/> 取锚点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>必须在"桌面没有升起来"的时候调用</strong>，理由见
    /// <see cref="_lastKnownPredecessor"/>：桌面一升起来，盖住便签的窗口就全被藏了，
    /// 那时再问就晚了。所以本方法的调用点全是"正常桌面"下的时机——前台换到某个窗口、
    /// 便签刚开出来、便签被拖动或缩放。
    /// </para>
    /// <para>
    /// 用户主动置顶的便签也照记不误：它们在 <see cref="PromoteForShowDesktop"/> 里
    /// 一律跳过，记下的值用不到，但"跳过"这件事让这里不必再判一次。
    /// </para>
    /// </remarks>
    private void RememberPredecessors()
    {
        foreach (NoteWindow window in _windows.Values)
        {
            if (!window.IsVisible)
            {
                continue;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;

            _lastKnownPredecessor[hwnd] = WindowInterop.GetZOrderPredecessor(hwnd);
        }
    }

    /// <summary>把所有可见的、非置顶的便签临时提到置顶档。</summary>
    /// <remarks>
    /// <para>
    /// 用户主动置顶的便签跳过：它们本来就在置顶档，再动一次只会在撤退时多一份
    /// "这张是不是我们提的" 的歧义。
    /// </para>
    /// <para>
    /// <strong>已经在 <see cref="_promotedForShowDesktop"/> 里的也要跳过。</strong>
    /// 桌面窗口连续两次成为前台时会再调一次本方法，而这时便签<em>已经</em>在置顶档上：
    /// 再问一次 <see cref="WindowInterop.GetZOrderPredecessor"/>，问到的只会是置顶区里
    /// 的邻居（任务栏、缩略图辅助窗口那一类），拿它覆盖掉先前记下的正确锚点，
    /// 撤退时就再也回不到原位——<see cref="WindowInterop.PlaceBehind"/> 判定锚点自己
    /// 就在置顶档，直接跳过归位，便签于是永久浮在所有普通窗口之上。
    /// </para>
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

            // 已经提过的不再提：锚点只在第一次提升之前是有效的。
            if (_promotedForShowDesktop.Contains(hwnd))
            {
                continue;
            }

            // 锚点用之前正常状态下采到的那个，不能在这里当场问一次——见 _lastKnownPredecessor，
            // 此刻盖住便签的窗口已经被「显示桌面」藏起来了，问出来的不是它。
            // 实在没采到（比如便签开出来之后一次前台切换都没发生过）才退回当场问。
            IntPtr predecessor = _lastKnownPredecessor.TryGetValue(hwnd, out IntPtr remembered)
                ? remembered
                : WindowInterop.GetZOrderPredecessor(hwnd);

            if (WindowInterop.MakeTopMost(hwnd))
            {
                _promotedForShowDesktop.Add(hwnd);
                _predecessorBeforePromotion[hwnd] = predecessor;
            }
        }
    }

    /// <summary>撤回 <see cref="PromoteForShowDesktop"/> 提升过的窗口，归位到提升前的 z 序。</summary>
    /// <remarks>
    /// <para>
    /// <strong>必须分两轮，不能"清一张、归位一张"地穿插着做。</strong>给 A 归位时若 B 还挂在
    /// 置顶档上，而 A 提升前恰好排在 B 下面（两张便签在屏幕上叠着），<c>SetWindowPos</c>
    /// 会连带把 A 也提拔进置顶档——这恰恰是 <see cref="WindowInterop.PlaceBehind"/> 要挡的
    /// 情况。先把所有窗口都退出置顶档，再统一归位，相互牵连就不存在了。
    /// 两轮之间不返回消息循环，用户看不到中间态。
    /// </para>
    /// <para>
    /// <strong>但这两轮插位只是把便签先摆个大概，落定要靠
    /// <see cref="SettleRestoredWindows"/>。</strong>原因见 <see cref="_restorePending"/>：
    /// 此刻「显示桌面」还在恢复窗口，插好的位置马上会被顶掉。这里如实记录待归位的清单、
    /// 启动复查，不要在这里就认为完事了。
    /// </para>
    /// </remarks>
    private void ReleaseTemporaryTopMost()
    {
        if (_promotedForShowDesktop.Count == 0)
        {
            // 这次前台切换跟「还原桌面」无关，是用户自己切到别的窗口上去了。
            // 上一轮的归位复查就此收手。
            CancelRestoreSettle();

            return;
        }

        foreach (IntPtr hwnd in _promotedForShowDesktop)
        {
            WindowInterop.ClearTopMost(hwnd);
        }

        _restorePending.Clear();

        foreach (IntPtr hwnd in _promotedForShowDesktop)
        {
            IntPtr anchor = _predecessorBeforePromotion.GetValueOrDefault(hwnd);

            WindowInterop.PlaceBehind(hwnd, anchor);

            // 锚点为零说明提升前就没问到邻居，PlaceBehind 会直接跳过——没有可复位的目标，
            // 放进复查清单只会让它空转到超时。
            if (anchor != IntPtr.Zero)
            {
                _restorePending[hwnd] = anchor;
            }
        }

        _promotedForShowDesktop.Clear();
        _predecessorBeforePromotion.Clear();

        if (_restorePending.Count > 0)
        {
            _restoreTicks = 0;
            EnsureRestoreTimer().Start();
        }
    }

    /// <summary>
    /// 复查便签有没有真的回到 <see cref="_restorePending"/> 记下的邻居后面，没回去就再插一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>只要"当下是对的"就收手是不够的——会漏掉后面还有的恢复。</strong>实测点任务栏
    /// 右下角那个按钮：还原那一瞬便签就已经落在正确位置，第一拍复查（+60ms）看到的正是"对的"，
    /// 于是收手；可恢复洪流在 +150ms、+200ms 又把它推到 z 序第 4、第 7 位，此后无人过问，
    /// 便签就停在最底下。而按 Win+D 时第一拍看到的恰好是错的，继续插，反倒正常——
    /// 用户于是以为"Win+D 行、按钮不行"，其实差别只在复查第一拍撞上了哪一种。
    /// </para>
    /// <para>
    /// 所以判据是<strong>看满整个窗口期</strong>：每一拍都重新比一次，谁的前驱还不是锚点就再插一次，
    /// 一直管到 <see cref="RestoreSettleMaxTicks"/> 用完为止，中途不因为"这会儿对了"而退出。
    /// 窗口期只比实测涨落时长（约 250ms）大四倍，成本是十几次指针比较。
    /// </para>
    /// <para>
    /// 一直管着会不会跟用户抢位置？不会。用户随后点到前台的窗口总是抬到锚点<em>之上</em>，
    /// 便签仍在锚点之后，前驱不变，于是这里什么都不做；而用户要是点回便签本身，前台就换了，
    /// 那次 <see cref="OnForegroundChanged"/> 已经把复查叫停（见 <see cref="ReleaseTemporaryTopMost"/>）。
    /// </para>
    /// <para>
    /// 收工时补采一次邻居：落定之后的这一刻才是「正常桌面」，此时采到的锚点下一次提升才用得上。
    /// </para>
    /// </remarks>
    private void SettleRestoredWindows()
    {
        // 桌面在复查期间又升起来了：便签正/即将挂在置顶档上，此时既不该插位、也不该采样。
        if (_promotedForShowDesktop.Count > 0)
        {
            CancelRestoreSettle();

            return;
        }

        foreach ((IntPtr hwnd, IntPtr anchor) in _restorePending)
        {
            if (WindowInterop.GetZOrderPredecessor(hwnd) != anchor)
            {
                WindowInterop.PlaceBehind(hwnd, anchor);
            }
        }

        if (++_restoreTicks < RestoreSettleMaxTicks)
        {
            return;
        }

        _restoreTimer?.Stop();
        _restorePending.Clear();
        RememberPredecessors();
    }

    /// <summary>停掉归位复查并忘掉待归位清单。</summary>
    private void CancelRestoreSettle()
    {
        _restoreTimer?.Stop();
        _restorePending.Clear();
    }

    private DispatcherTimer EnsureRestoreTimer()
    {
        if (_restoreTimer is null)
        {
            _restoreTimer = new DispatcherTimer { Interval = RestoreSettleInterval };
            _restoreTimer.Tick += (_, _) => SettleRestoredWindows();
        }

        return _restoreTimer;
    }

    private void OnGeometryChanged(Guid noteId)
    {
        if (!_windows.TryGetValue(noteId, out NoteWindow? window))
        {
            return;
        }

        CaptureGeometry(noteId, window.ViewModel.Layout);
        _layout.MarkDirtyAndScheduleFlush();

        // 挪个位置就可能换了一个"盖住它的窗口"，锚点跟着更新。
        RememberPredecessors();
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

        // 句柄会随窗口一起失效，留在两份记录里的话，下次撤退时就是在往一个
        // 已经销毁的句柄上调 SetWindowPos。句柄还可能被系统复用。
        IntPtr hwnd = new WindowInteropHelper(window).Handle;

        _promotedForShowDesktop.Remove(hwnd);
        _predecessorBeforePromotion.Remove(hwnd);
        _lastKnownPredecessor.Remove(hwnd);
        _restorePending.Remove(hwnd);

        // 退出过程中被关掉的那些，不算「用户关掉了这张便签」（见 BeginShutdown）。
        // 退出时窗口仍然要解除映射、句柄仍然要清干净——跳过只是不回写 IsOpen。
        if (!_isShuttingDown)
        {
            _noteService.MarkNoteClosed(noteId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _foreground.ForegroundChanged -= OnForegroundChanged;
        _foreground.Dispose();
        CancelRestoreSettle();
        _promotedForShowDesktop.Clear();
        _predecessorBeforePromotion.Clear();
        _lastKnownPredecessor.Clear();
    }

    /// <summary>窗口当前的 DIP → 物理像素缩放系数（1.0 = 100%，1.5 = 150%）。</summary>
    private static double DeviceScale(Window window)
    {
        PresentationSource? source = PresentationSource.FromVisual(window);
        double m11 = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;

        return m11 > 0 ? m11 : 1.0;
    }
}
