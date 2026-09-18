// 命名空间与 CsWin32 的 Windows.Win32 撞名，只能用 global:: 前缀，理由见 MonitorEnumerator.cs 顶部。
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.UI.Accessibility;

namespace LumiMemo.Infrastructure.Windows;

/// <summary>
/// 监听「前台窗口换成了谁」，用来识别「显示桌面」（Win+D）的发生与结束（§13.6）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么非得是事件钩子。</strong>显示桌面不产生任何可以轮询的信号：桌面窗口
/// （<c>Progman</c>）一直在那儿，便签也从头到尾没被最小化过。唯一可靠的判据是
/// 「前台变成了桌面窗口」——而这只有 <c>SetWinEventHook</c> 收得到。
/// </para>
/// <para>
/// <strong>它不属于附录 D.7 禁用的 <c>SetWindowsHookEx</c>。</strong>那一条禁的是往
/// 别人的消息流里插钩子（会让整个桌面卡顿、还会被安全软件盯上）；这里的
/// <c>EVENT_SYSTEM_FOREGROUND</c> 是系统提供的<em>通知</em>，只订阅一个事件，
/// 而且用 <c>WINEVENT_OUTOFCONTEXT</c> 注册——回调走我们自己的消息队列，
/// 不注入任何进程。
/// </para>
/// <para>
/// <strong>必须在有消息泵的线程上调用 <see cref="Start"/></strong>，
/// 也就是 UI 线程：<c>WINEVENT_OUTOFCONTEXT</c> 的回调是投递到注册线程的消息队列上的。
/// 因此在别的线程注册，回调就永远不会被派发。
/// </para>
/// </remarks>
public sealed class ShellForegroundWatcher : IDisposable
{
    /// <summary>
    /// 得用字段拴住这个委托实例。原生侧只拿得到一个函数指针，托管侧一旦把它回收掉，
    /// 下一次事件就是往一块已经释放的内存里跳——崩溃点还离这里很远，极其难查。
    /// </summary>
    private WINEVENTPROC? _callback;

    private UnhookWinEventSafeHandle? _hook;

    /// <summary>前台窗口变了。参数是新前台窗口的句柄。</summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>
    /// 开始监听。重复调用是空操作。
    /// </summary>
    /// <returns>钩子是否装上。</returns>
    /// <remarks>
    /// 不传 <c>WINEVENT_SKIPOWNPROCESS</c>：用户点回自家的便签窗口时，前台同样离开了桌面，
    /// 我们也得把临时置顶撤掉。滤掉本进程的事件会让便签在那种情况下永远留在最上层
    /// （实测复现过）。代价只是多收几次事件，回调里只有两次指针比较。
    /// </remarks>
    public bool Start()
    {
        if (_hook is not null)
        {
            return true;
        }

        _callback = OnForegroundChanged;

        _hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            null,
            _callback,
            0,
            0,
            PInvoke.WINEVENT_OUTOFCONTEXT);

        if (_hook.IsInvalid)
        {
            _callback = null;
            _hook = null;

            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // UnhookWinEventSafeHandle 的 ReleaseHandle 会替我们调 UnhookWinEvent。
        // 必须与 Start 在同一个线程上释放，见类注释。
        _hook?.Dispose();
        _hook = null;
        _callback = null;
    }

    private void OnForegroundChanged(
        HWINEVENTHOOK hWinEventHook,
        uint @event,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime) => ForegroundChanged?.Invoke(hwnd);
}
