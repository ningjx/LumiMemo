// 命名空间与 CsWin32 的 Windows.Win32 撞名，只能用 global:: 前缀，理由见 MonitorEnumerator.cs 顶部。
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.UI.WindowsAndMessaging;

namespace LumiMemo.Infrastructure.Windows;

/// <summary>
/// 窗口层面的 Win32 包装。App 层要动 HWND 时只经这里（决策 3：Win32 全关在 Infrastructure 里）。
/// </summary>
/// <remarks>
/// <para>
/// 参数收 <see cref="IntPtr"/> 而不是 WPF 的 <c>Window</c>：本工程目标框架是不带
/// <c>-windows</c> 后缀的 <c>net10.0</c>，引不到 WPF 类型。
/// </para>
/// </remarks>
public static class WindowInterop
{
    /// <summary>
    /// <c>HWND_TOPMOST</c>。出现在元数据里的只有它的兄弟 <c>HWND_TOP</c>，
    /// 而那两个"置顶档"的哨兵值是宏，CsWin32 不会为它们生成常量。
    /// </summary>
    private static readonly HWND HwndTopMost = new(-1);

    /// <summary><c>HWND_NOTOPMOST</c>，见 <see cref="HwndTopMost"/>。</summary>
    private static readonly HWND HwndNoTopMost = new(-2);

    private const SET_WINDOW_POS_FLAGS MoveNothing =
        SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;

    /// <summary>桌面窗口（<c>Progman</c>）。「显示桌面」时前台就是它。</summary>
    public static IntPtr GetShellWindowHandle() => PInvoke.GetShellWindow();

    /// <summary>
    /// 窗口是否属于本进程。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>属于本进程返回 <see langword="true"/>；句柄为空或已失效返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 用来把「用户点了别处」和「系统自己在还原窗口」区分开，见
    /// <c>WindowManager.OnForegroundChanged</c>。
    /// </remarks>
    public static bool BelongsToCurrentProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        PInvoke.GetWindowThreadProcessId(new HWND(hwnd), out uint processId);

        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 把窗口临时提到置顶档。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>调用是否成功。</returns>
    /// <remarks>
    /// <para>
    /// <strong>这是唯一能盖过"显示桌面"的手段</strong>（§13.6）。桌面升起来时是被抬到
    /// 置顶档的，而普通 z 序那一档够不到它——实测 <c>SetWindowPos(HWND_TOP)</c> 事后调用
    /// 完全压不过（窗口中心处 <c>WindowFromPoint</c> 拿到的仍是 <c>FolderView</c>），
    /// 换到"桌面刚成为前台"的那一刻调用也只在头两秒有效，而且它还会把前台抢回便签。
    /// </para>
    /// <para>
    /// 三个 <c>SWP_NO*</c> 一个都不能少：<c>SWP_NOMOVE|SWP_NOSIZE</c> 保持几何不变，
    /// <c>SWP_NOACTIVATE</c> 保证不抢前台——用户这时候正站在桌面上，抢走焦点的话
    /// 接下来的按键会打进便签里。
    /// </para>
    /// </remarks>
    public static bool MakeTopMost(IntPtr hwnd) =>
        hwnd != IntPtr.Zero
        && PInvoke.SetWindowPos(new HWND(hwnd), HwndTopMost, 0, 0, 0, 0, MoveNothing);

    /// <summary>
    /// 撤销 <see cref="MakeTopMost"/>，并让窗口排在 <paramref name="foregroundAnchor"/> 之后。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="foregroundAnchor">
    /// 用户刚刚切过去的那个前台窗口；传 <see cref="IntPtr.Zero"/> 表示"不动 z 序"。
    /// </param>
    /// <returns>两步是否都成功。</returns>
    /// <remarks>
    /// <para>
    /// <strong>为什么必须是两步。</strong><c>HWND_NOTOPMOST</c> 是唯一能清掉
    /// <c>WS_EX_TOPMOST</c> 的手段，可它的语义是"放到<em>所有</em>非置顶窗口之上"
    /// ——单用它，便签会在用户刚点开某个窗口的瞬间反过来盖住那个窗口（实测复现）。
    /// 所以清完之后必须立刻把它插到那个窗口后面。两步之间不返回消息循环，
    /// 用户看不到中间态。
    /// </para>
    /// <para>
    /// <strong><c>SWP_NOZORDER</c> 是死路，别试。</strong>用它配 <c>HWND_NOTOPMOST</c>
    /// 想"只清标志位、不动 z 序"，实测调用返回成功而 <c>WS_EX_TOPMOST</c> 纹丝不动
    /// ——标记位和 z 序是一体的，绕不开。
    /// </para>
    /// <para>
    /// <paramref name="foregroundAnchor"/> 等于 <paramref name="hwnd"/> 时只做第一步：
    /// 用户点的就是这张便签，它本来就该在最上面，把"自己插到自己后面"没有意义。
    /// </para>
    /// </remarks>
    public static bool ClearTopMost(IntPtr hwnd, IntPtr foregroundAnchor)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        bool cleared = PInvoke.SetWindowPos(new HWND(hwnd), HwndNoTopMost, 0, 0, 0, 0, MoveNothing);

        if (foregroundAnchor == IntPtr.Zero || foregroundAnchor == hwnd)
        {
            return cleared;
        }

        // 桌面窗口（Progman）不作为锚点：它一旦离开前台就会自己降下去，
        // 把自己排在它后面等于把便签丢到所有窗口底下。
        if (foregroundAnchor == GetShellWindowHandle())
        {
            return cleared;
        }

        return PInvoke.SetWindowPos(new HWND(hwnd), new HWND(foregroundAnchor), 0, 0, 0, 0, MoveNothing)
            && cleared;
    }
}
