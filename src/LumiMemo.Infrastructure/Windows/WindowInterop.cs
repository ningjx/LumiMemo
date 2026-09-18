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

    /// <summary>桌面窗口（<c>Progman</c>）。「显示桌面」期间它就是前台。</summary>
    public static IntPtr GetShellWindowHandle() => PInvoke.GetShellWindow();

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
    /// 取 z 序里紧挨着这个窗口<strong>上面</strong>的那个窗口，也就是用户眼里此刻盖住它的那个。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>
    /// 上一个"算数的"窗口；往上没有这样的窗口、已到 z 序顶端，或句柄无效时返回
    /// <see cref="IntPtr.Zero"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 临时置顶<strong>之前</strong>调用，把结果留给 <see cref="PlaceBehind"/>，
    /// 就是"从哪儿来回哪儿去"里的那个"哪儿"。
    /// </para>
    /// <para>
    /// <strong>它只返回 <see cref="PlaceBehind"/> 会接受的锚点</strong>，三种窗口一律跳过，
    /// 于是"取回来的锚点用不了"这件事在源头就不存在了：
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <strong>隐藏窗口。</strong>便签上面常挤着一串不可见的辅助窗口——每个有过输入焦点的
    ///     线程都挂着一个 <c>IME</c> 与 <c>MSCTFIME UI</c>，实测一张便签上面能连着压五六个。
    ///     它们是可见序列里的透明人：用户看不见，却实实在在占着 z 序。拿它们当锚点，便签会落进
    ///     一个"看着像在最上面"的位置——排到浏览器之上、却排在这些幽灵之下。
    ///   </item>
    ///   <item>
    ///     <strong>桌面窗口（<c>Progman</c>）。</strong>这条是实测出来的，而且它才是
    ///     "便签还原后跑到最底下"的元凶：本方法是在<em>前台已经变成桌面之后</em>才被调用的
    ///     （<c>WINEVENT_OUTOFCONTEXT</c> 的回调走消息队列，而"显示桌面"早就把桌面窗口抬到
    ///     普通窗口之上了）。于是从便签往上第一个可见窗口正是被抬起来的桌面，便签被插到它后面
    ///     ——桌面本来就是普通档最底下那个，插到它后面就是全屏幕最底下。锚点的意义是
    ///     "用户眼里盖住便签的那个窗口"，而桌面是背景板，从来不是"盖住"它的东西。
    ///   </item>
    ///   <item>
    ///     <strong>置顶档的窗口。</strong>把便签插到置顶窗口后面会连带把它也提拔进置顶档，
    ///     而这次错误的提拔再也撤不掉，见 <see cref="PlaceBehind"/>。跳过它们并不会丢失
    ///     精确度：便签本来就不可能排在置顶窗口之下，跳过之后要么找到一个更靠下的正经锚点，
    ///     要么走到顶端返回 <see cref="IntPtr.Zero"/>，让便签留在普通档最上面——那正是它该待的地方。
    ///   </item>
    /// </list>
    /// </remarks>
    public static IntPtr GetZOrderPredecessor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr shell = PInvoke.GetShellWindow();

        IntPtr current = PInvoke.GetWindow(new HWND(hwnd), GET_WINDOW_CMD.GW_HWNDPREV);

        while (current != IntPtr.Zero
            && (!PInvoke.IsWindowVisible(new HWND(current)) || current == shell || IsTopMost(current)))
        {
            current = PInvoke.GetWindow(new HWND(current), GET_WINDOW_CMD.GW_HWNDPREV);
        }

        return current;
    }

    /// <summary>
    /// 撤掉临时置顶：清 <c>WS_EX_TOPMOST</c>，不动 z 序。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>调用是否成功。</returns>
    /// <remarks>
    /// <para>
    /// <c>HWND_NOTOPMOST</c> 是唯一能清掉 <c>WS_EX_TOPMOST</c> 的手段，可它的语义是
    /// "放到<em>所有</em>非置顶窗口之上"——单用它，便签虽然退出了置顶档，却会浮到
    /// 一切普通窗口的最上面。要让它落回原位，得紧接着
    /// <see cref="PlaceBehind"/> 插回提升前那个邻居后面。
    /// </para>
    /// <para>
    /// <strong><c>SWP_NOZORDER</c> 是死路，别试。</strong>用它配 <c>HWND_NOTOPMOST</c>
    /// 想"只清标志位、不动 z 序"，实测调用返回成功而 <c>WS_EX_TOPMOST</c> 纹丝不动
    /// ——标记位和 z 序是一体的，绕不开。
    /// </para>
    /// </remarks>
    public static bool ClearTopMost(IntPtr hwnd) =>
        hwnd != IntPtr.Zero
        && PInvoke.SetWindowPos(new HWND(hwnd), HwndNoTopMost, 0, 0, 0, 0, MoveNothing);

    /// <summary>
    /// 把窗口插到 <paramref name="after"/> 的后面，也就是排到它的<strong>下面</strong>去。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="after">
    /// 要排在它后面的那个窗口；<see cref="IntPtr.Zero"/> 表示不动 z 序。
    /// </param>
    /// <returns>是否成功；下面几种"跳过"也算成功。</returns>
    /// <remarks>
    /// <para>
    /// 以下情况只清过标志、不插 z 序：
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <paramref name="after"/> 为空——窗口提升前本来就在 z 序最顶，没有"下面"可插，
    ///     而它此刻的位置（普通档最上面）正是原位。
    ///   </item>
    ///   <item>
    ///     <strong><paramref name="after"/> 自己就在置顶档</strong>——把窗口插到一个置顶窗口
    ///     后面，<c>SetWindowPos</c> 会连带把它也提拔进置顶档，而且这次错误的提拔
    ///     <em>再也撤不掉</em>（<c>PromoteForShowDesktop</c> 会跳过已置顶的窗口，
    ///     而撤退只处理自己提升过的那些）。
    ///   </item>
    ///   <item>
    ///     <paramref name="after"/> 已经失效或不再可见——提升与撤退之间隔着整个"显示桌面"，
    ///     期间系统可能已经把那个窗口销毁了，句柄还可能被复用；输入法那类辅助窗口也会
    ///     随时显隐。
    ///   </item>
    ///   <item>
    ///     <paramref name="after"/> 就是自己——窗口会落进"已激活却排在别人之下"的
    ///     非自然状态，之后再也提不上去（实测复现）。
    ///   </item>
    /// </list>
    /// </remarks>
    public static bool PlaceBehind(IntPtr hwnd, IntPtr after)
    {
        if (hwnd == IntPtr.Zero || after == IntPtr.Zero || after == hwnd)
        {
            return true;
        }

        if (!PInvoke.IsWindow(new HWND(after)) || !PInvoke.IsWindowVisible(new HWND(after)) || IsTopMost(after))
        {
            return true;
        }

        return PInvoke.SetWindowPos(new HWND(hwnd), new HWND(after), 0, 0, 0, 0, MoveNothing);
    }

    /// <summary>窗口是否在置顶档（带 <c>WS_EX_TOPMOST</c>）。</summary>
    private static bool IsTopMost(IntPtr hwnd) =>
        (PInvoke.GetWindowLongPtr(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
            & (nint)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;
}
