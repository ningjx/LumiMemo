using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 显示器信息的来源（§13.8）。实现在 Infrastructure，用 CsWin32 调 Win32。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽出这一层不是为了「将来换平台」，而是为了让恢复算法能在无头测试里跑：
/// <see cref="Math.LayoutMath"/> 只做纯计算，显示器数据由本接口喂进来。
/// </para>
/// <para>
/// 每个方法都是<strong>现查现取</strong>，返回的是快照。显示器可以被热插拔，
/// 不要缓存返回的对象然后假设它一直有效。
/// </para>
/// </remarks>
public interface IDisplayProvider
{
    /// <summary>当前所有显示器，含已被系统关闭但仍在虚拟屏幕上的。</summary>
    IReadOnlyList<DisplaySnapshot> All { get; }

    /// <summary>主显示器。显示器消失后要把窗口层叠回它，因此必须有这个入口。</summary>
    DisplaySnapshot Primary { get; }

    /// <summary>
    /// 找到包含指定物理像素点的显示器。
    /// </summary>
    /// <param name="xPx">物理像素横坐标，可以是负数（副屏在主屏左侧时）。</param>
    /// <param name="yPx">物理像素纵坐标。</param>
    /// <returns>
    /// 包含该点的显示器；<strong>一个都不是时返回 <see langword="null"/></strong>。
    /// </returns>
    /// <remarks>
    /// 刻意<strong>不</strong>模仿 <c>MonitorFromPoint</c> 的 <c>MONITOR_DEFAULTTONEAREST</c> 行为
    /// （找不到就返回最近的一台）。那条语义会把「原显示器已拔掉」和「窗口本来就在主屏上」
    /// 变成同一个结果，而两者的处理方式完全不同：前者要层叠重排并记日志，后者什么都别做。
    /// 返回 <see langword="null"/> 才能让调用方区分开（§13.8「显示器不存在时」）。
    /// </remarks>
    DisplaySnapshot? FindDisplayContaining(double xPx, double yPx);
}
