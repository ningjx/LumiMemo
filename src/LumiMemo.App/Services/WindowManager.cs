using System.Windows;
using LumiMemo.App.Abstractions;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;

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
public sealed class WindowManager : IWindowManager
{
    private readonly LayoutService _layout;
    private readonly INoteService _noteService;
    private readonly AutoSaveService _autoSaveService;
    private readonly IDisplayProvider _displays;

    private readonly Dictionary<Guid, NoteWindow> _windows = [];

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

    public WindowManager(
        LayoutService layout,
        INoteService noteService,
        AutoSaveService autoSaveService,
        IDisplayProvider displays)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(autoSaveService);
        ArgumentNullException.ThrowIfNull(displays);

        _layout = layout;
        _noteService = noteService;
        _autoSaveService = autoSaveService;
        _displays = displays;
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

    /// <inheritdoc />
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
        if (!_windows.Remove(noteId))
        {
            return;
        }

        _noteService.MarkNoteClosed(noteId);
    }

    /// <summary>窗口当前的 DIP → 物理像素缩放系数（1.0 = 100%，1.5 = 150%）。</summary>
    private static double DeviceScale(Window window)
    {
        PresentationSource? source = PresentationSource.FromVisual(window);
        double m11 = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;

        return m11 > 0 ? m11 : 1.0;
    }
}
