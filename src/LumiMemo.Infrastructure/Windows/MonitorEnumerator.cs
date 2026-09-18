// 本文件自己的命名空间叫 LumiMemo.Infrastructure.Windows，与 CsWin32 的 Windows.Win32 撞名：
// 从 LumiMemo.Infrastructure.Windows 里去找 "Windows"，先命中的是它自己——因为
// LumiMemo.Infrastructure.Windows 正是 LumiMemo.Infrastructure 的一个成员，
// 而内层命名空间找不到时就会往外层找。于是 Windows.Win32 被解析成
// LumiMemo.Infrastructure.Windows.Win32，编译失败。
// 换命名空间躲不掉（只要外层有个叫 Windows 的兄弟就一样），只能用 global:: 前缀。
// 与 LumiMemo.Core.Math 里 System.Math 那个坑是同一类，区别只在那次能用 using static 绕开。
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.Graphics.Gdi;
using global::Windows.Win32.UI.HiDpi;

namespace LumiMemo.Infrastructure.Windows;

/// <summary>
/// <see cref="IDisplayProvider"/> 的 CsWin32 实现（§13.8）。
/// </summary>
/// <remarks>
/// <para>
/// 全解决方案<strong>唯一</strong>调 <c>EnumDisplayMonitors</c> / <c>GetMonitorInfo</c> /
/// <c>EnumDisplayDevices</c> / <c>GetDpiForMonitor</c> 的地方。别处要显示器信息只能经
/// <see cref="IDisplayProvider"/>——那样无头测试才顶得上替身。
/// </para>
/// <para>
/// <strong>每次读都重新枚举</strong>（接口的约定）：显示器可以热插拔，
/// 缓存一份清单的结果是「副屏拔掉了，窗口还往那个方向跑」。
/// </para>
/// <para>
/// 拿到的坐标是不是真的物理像素，取决于<strong>进程的 DPI 感知级别</strong>：
/// <c>PROCESS_DPI_UNAWARE</c> 的进程拿到的矩形被系统虚拟化过，而且
/// <c>GetDpiForMonitor</c> 会一律回 96。本程序是 WPF on .NET 5+，默认 PerMonitorV2，
/// 满足这个前提；把本库塞进别的宿主进程时这条前提就没了。
/// </para>
/// </remarks>
public sealed class MonitorEnumerator : IDisplayProvider
{
    /// <summary>问不出 DPI 时的兜底值，即 100%（§13.8）。</summary>
    private const uint FallbackDpi = 96;

    /// <inheritdoc />
    /// <remarks>
    /// 枚举到的是<strong>虚拟屏幕上占着位置的</strong>显示器，其中可能包含镜像驱动挂出来的
    /// 伪显示器。不做过滤：它与真实那块矩形完全重合，而设备路径重复的那一份会在
    /// <see cref="TryRead"/> 里去重丢掉，剩下能留下的本来也无害。
    /// </remarks>
    public IReadOnlyList<DisplaySnapshot> All
    {
        get
        {
            IReadOnlyList<HMONITOR> handles = CollectMonitorHandles();
            var result = new List<DisplaySnapshot>(handles.Count);
            var seenDeviceIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (HMONITOR handle in handles)
            {
                if (TryRead(handle, seenDeviceIds, out DisplaySnapshot snapshot))
                {
                    result.Add(snapshot);
                }
            }

            return result;
        }
    }

    /// <inheritdoc />
    /// <exception cref="IndexOutOfRangeException">一台显示器都枚举不到。</exception>
    /// <remarks>
    /// 桌面会话里至少有一台显示器，因此正常路径上 <see cref="All"/> 不会是空的；
    /// 真为空时没有任何合理的兜底值，直接抛比编一个主显示器出来强。
    /// </remarks>
    public DisplaySnapshot Primary
    {
        get
        {
            IReadOnlyList<DisplaySnapshot> displays = All;

            return displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 判定转交 <see cref="LayoutMath.FindDisplayContaining"/>：命中规则在 Core 里只有一处实现
    /// （左闭右开、只看整块屏幕不看工作区），这里再抄一份迟早会与它分歧。
    /// 也刻意<strong>不</strong>用 <c>MonitorFromPoint</c> 的 <c>MONITOR_DEFAULTTONEAREST</c> 语义，
    /// 理由见接口上的注释。
    /// </remarks>
    public DisplaySnapshot? FindDisplayContaining(double xPx, double yPx) =>
        LayoutMath.FindDisplayContaining(All, xPx, yPx);

    /// <summary>问系统要当前所有显示器的句柄。</summary>
    /// <remarks>
    /// 单独拆出来只是为了把 <c>unsafe</c> 关在这一处：<c>MONITORENUMPROC</c> 的签名带
    /// <c>RECT*</c>，因此这个委托类型本身是 unsafe 的，连把 lambda 转成它都需要不安全上下文。
    /// </remarks>
    private static unsafe IReadOnlyList<HMONITOR> CollectMonitorHandles()
    {
        var handles = new List<HMONITOR>();

        // 回调返回 true 才会继续枚举下一台，返回 false 会当场停住（剩下的就没了）。
        // 委托由编译器生成的闭包对象持有，同步调用期间不会被回收，不用自己固定。
        PInvoke.EnumDisplayMonitors(
            default,
            null,
            (hmonitor, _, _, _) =>
            {
                handles.Add(hmonitor);
                return true;
            },
            default);

        return handles;
    }

    /// <summary>读一台显示器的信息。</summary>
    /// <param name="handle">本次枚举里的一个显示器句柄。</param>
    /// <param name="seenDeviceIds">
    /// 本次枚举已产出的设备路径。重复的会被跳过，否则同一块屏幕会在
    /// <c>displays</c> 表里占两个键，而其中一半的布局改动会写进另一个键里去。
    /// </param>
    /// <param name="snapshot">读成功时的结果。</param>
    /// <returns>读到一台没读过的显示器时为 <see langword="true"/>。</returns>
    private static unsafe bool TryRead(
        HMONITOR handle,
        HashSet<string> seenDeviceIds,
        out DisplaySnapshot snapshot)
    {
        snapshot = null!;

        MONITORINFOEXW info = default;

        // cbSize 决定系统往哪个结构体里写：填 MONITORINFO 的大小就只回填前半截，
        // szDevice 是空的，而设备名正是接着问 EnumDisplayDevices 的唯一线索。
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);

        // 只能用 MONITORINFO* 那个重载：CsWin32 没有为 MONITORINFOEXW 生成带 ref 的友好重载
        // （那个只认 MONITORINFO）。靠「MONITORINFOEXW 的第一个字段就是 MONITORINFO」做指针转换，
        // 这也正是文档给的用法——两种结构体共用同一个函数，靠 cbSize 区分。
        if (!PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
        {
            // 枚举与逐台查询之间显示器被拔掉会走到这里。跳过它让其余照常返回，
            // 而不是把整次枚举作废——那样一台显示器的抖动会让所有便签都重排。
            return false;
        }

        string adapterName = ReadFixedString(info.szDevice.AsSpan());

        DISPLAY_DEVICEW device = default;
        device.cb = (uint)sizeof(DISPLAY_DEVICEW);

        // 带上 EDD_GET_DEVICE_INTERFACE_NAME，DeviceID 才是
        // "\\?\DISPLAY#DEL41A6#...#{e6f07b5f-...}" 这种跨会话稳定的设备接口路径（§8.3）。
        // 不带的话拿到的是 "MONITOR\DEL41A6\{...}"——那是卷名不是接口路径，当身份用不住（§13.8）。
        bool hasDevice = PInvoke.EnumDisplayDevices(
            adapterName,
            0,
            ref device,
            PInvoke.EDD_GET_DEVICE_INTERFACE_NAME);

        string deviceId = hasDevice ? ReadFixedString(device.DeviceID.AsSpan()) : string.Empty;

        if (deviceId.Length == 0)
        {
            // 没接显示器、或者跑在远程会话里时问不到接口路径，退回 GDI 设备名。
            // 它不如接口路径稳，但每台显示器各不相同，不至于让两台的布局互相覆盖。
            deviceId = adapterName;
        }

        if (!seenDeviceIds.Add(deviceId))
        {
            return false;
        }

        string friendlyName = hasDevice ? ReadFixedString(device.DeviceString.AsSpan()) : string.Empty;

        if (friendlyName.Length == 0)
        {
            friendlyName = adapterName;
        }

        snapshot = new DisplaySnapshot(
            deviceId,
            friendlyName,
            ToPixelRect(info.monitorInfo.rcMonitor),
            ToPixelRect(info.monitorInfo.rcWork),
            ReadDpi(handle))
        {
            IsPrimary = (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0,
        };

        return true;
    }

    /// <summary>问这台显示器的<strong>当前</strong> DPI。</summary>
    /// <remarks>
    /// 用 <c>MDT_EFFECTIVE_DPI</c>：它才是用户「缩放到 150%」之后生效的那个值（§13.8）。
    /// 问不出来时退回 100%，而不是让整台显示器作废——位置尺寸仍然是对的，
    /// 只是跨屏缩放会差一档，比「这台显示器不存在、便签全被层叠到主屏」温和得多。
    /// </remarks>
    private static uint ReadDpi(HMONITOR handle)
    {
        if (PInvoke
                .GetDpiForMonitor(handle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _)
                .Succeeded
            && dpiX > 0)
        {
            return dpiX;
        }

        return FallbackDpi;
    }

    /// <summary><c>RECT</c> 是「左上右下」，转成「左上 + 宽高」。</summary>
    private static PixelRect ToPixelRect(RECT rect) =>
        new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);

    /// <summary>从定长字符缓冲区里读出以 NUL 结尾的字符串。</summary>
    private static string ReadFixedString(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');

        return end < 0 ? new string(buffer) : new string(buffer[..end]);
    }
}
