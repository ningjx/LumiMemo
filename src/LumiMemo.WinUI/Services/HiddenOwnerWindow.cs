using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace LumiMemo.WinUI.Services;

/// <summary>
/// 给一个窗口挂上隐藏的 **owner**（Win32 所有者窗口）：这个窗口从此**不占任务栏、但仍进 Alt+Tab**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <c>AppWindow.IsShownInSwitchers</c></b>：它是"系统那两个列表一起开关"
/// （文档原话：appear in various system representations, such as ALT+TAB and taskbar），
/// 关掉任务栏的同时把 Alt+Tab 也关掉了——而我们要的恰恰是这两处分家。
/// 更麻烦的是它内部走 shell 的 <c>ITaskbarList</c>（<c>HrInit</c> + <c>AddTab</c>/<c>DeleteTab</c>，
/// 见 microsoft-ui-xaml#8596）：调用它等于让 shell 重新登记这个窗口，
/// 会把 owner 摘掉的任务栏按钮又装回来，explorer 不响应时还会抛异常。
/// </para>
/// <para>
/// <b>分家的依据是两套不同的规则</b>：
/// 任务栏只看「有没有 owner」——MS Learn《The Taskbar § Managing Taskbar Buttons》：
/// 有 owner 的窗口不生成任务栏按钮（"create a hidden window and make this hidden window
/// the owner of your visible window"）；Alt+Tab 则是"每个 owner 簇挑一个可见代表"——
/// Raymond Chen《Which windows appear in the Alt+Tab list?》：隐藏的 owner 自己不露脸，
/// 它名下的可见窗口照旧进列表。
/// </para>
/// <para>
/// <b>每个窗口各配一个 owner</b>：Alt+Tab 每个 owner 簇只挑一个代表窗口，
/// 共用一个 owner 的话几张便签会互相顶掉，列表里只剩最近激活的那一张。
/// </para>
/// <para>
/// <b>owner 只建不删、用完还池</b>：Win32 规定 owner 一销毁，它名下的窗口跟着销毁；
/// 便签关窗时 WinUI 正在拆自己的 XAML，外部抢先销毁便签窗口会让协同层踩到已释放内存
/// （实机：关便签时 <c>AcrylicBackdrop.Dispose → DesktopAcrylicController.Dispose</c> 0xC0000005）。
/// 所以关窗路径**完全不碰**这个 owner，只把它还回池子、留给下一张便签复用——
/// 池子上限 = 同时打开的便签数，不会随开关次数增长；进程退出时系统一并回收。
/// </para>
/// <para>
/// owner 用**裸 Win32 窗口**而不是 WinUI 的 <see cref="Window"/>：它不需要 XAML，
/// 生命周期完全由本类掌握。本仓吃过 XAML 窗口拆解时序的苦头（退出时 0xC000027B），
/// 别再往那条路上加窗口。
/// </para>
/// </remarks>
internal sealed class HiddenOwnerWindow : IDisposable
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const uint WsPopup = 0x80000000;

    /// <summary>闲置 owner 池（只在 UI 线程上用：开窗借一个、关窗还一个，见类型注释）。</summary>
    private static readonly Stack<IntPtr> IdleOwners = new();

    private IntPtr _hwnd;

    private HiddenOwnerWindow(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// 给 <paramref name="target"/> 挂一个隐藏 owner；返回的实例负责收摊（还池）。
    /// <b>要在目标窗口显示之前调</b>——任务栏只在窗口"变为可见"那一刻判定它够不够格。
    /// </summary>
    /// <returns>挂上了就返回实例；建不出 owner 窗口时返回 <see langword="null"/>（退化成有任务栏按钮）。</returns>
    public static HiddenOwnerWindow? AttachTo(Window target)
    {
        ArgumentNullException.ThrowIfNull(target);

        IntPtr owner = IdleOwners.Count > 0 ? IdleOwners.Pop() : CreateOwnerWindow();
        if (owner == IntPtr.Zero)
        {
            return null;
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(target);

        // 两个会坏事的外壳样式都清掉：WS_EX_APPWINDOW 会把窗口硬塞进任务栏（有没有 owner 都一样），
        // WS_EX_TOOLWINDOW 会把它从 Alt+Tab 里踢出去。两个都不能留。
        long exStyle = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(exStyle & ~WsExAppWindow & ~WsExToolWindow));

        // GWLP_HWNDPARENT 对**顶层窗口**设的是 owner（不是父窗口），正是这里要的。
        SetWindowLongPtrW(hwnd, GwlpHwndParent, owner);
        return new HiddenOwnerWindow(owner);
    }

    /// <summary>把 owner 还回池子（<b>不销毁</b>——销毁会连带销毁它名下的便签窗口，见类型注释）。</summary>
    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        IdleOwners.Push(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>建一个永不显示的顶层窗口当 owner；失败返回 0（调用方退化成有任务栏按钮）。</summary>
    private static IntPtr CreateOwnerWindow()
    {
        IntPtr owner = CreateWindowExW(0, "Static", "LumiMemoSwitcherOwner", WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (owner == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[窗口归属] 隐藏 owner 建不出来（错误码 {Marshal.GetLastWin32Error()}）：" +
                "便签会照旧占一个任务栏按钮。");
        }

        return owner;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
