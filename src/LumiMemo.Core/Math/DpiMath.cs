// 本文件所在的命名空间就叫 LumiMemo.Core.Math。在那里写 Math.Round 会被解析成
// 「那个命名空间的成员 Round」，而它当然没有，于是编译失败——外层命名空间的成员
// 比 using 别名优先，所以 using Math = System.Math; 这种写法救不了场。
// using static 导入的是成员名（Round、Min、Abs），根本不经过 Math 这个名字。
using static System.Math;

using LumiMemo.Core.Models;

namespace LumiMemo.Core.Math;

/// <summary>
/// DPI 换算（§13.8）。全是纯函数，没有 IO、没有状态。
/// </summary>
/// <remarks>
/// <para>
/// <strong>两个换算系数的方向必须分清，这是本项目最容易写反的地方</strong>：
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="Scale"/> 是「保存时的 DPI → 当前的 DPI」之比，用于用户拖出来的尺寸。
///     100% 屏上存的 360px 宽的便签，拖到 150% 屏后要变成 540px，视觉宽度才一致。
///   </item>
///   <item>
///     <see cref="DipToPixelScale"/> 是「DIP → 物理像素」，分母固定 96。
///     用于界面上写死的尺寸常量（如标题条 36 DIP）。
///   </item>
/// </list>
/// <para>
/// 把两者混用是一个安静的 bug：在 100% 屏上开发时两者恰好都等于 1，肉眼看不出任何异常，
/// 只有到混合 DPI 的机器上才暴露。因此两个方法分开命名，调用点必须自己想清楚用哪个。
/// </para>
/// </remarks>
public static class DpiMath
{
    /// <summary>基准 DPI。Windows 的「100% 缩放」即 96 DPI。</summary>
    public const uint DefaultDpi = 96;

    /// <summary>
    /// 把 <c>0</c> 这个非法值归一到 <see cref="DefaultDpi"/>。
    /// </summary>
    /// <remarks>
    /// 手改过的旧版 <c>layout.json</c> 里 <c>dpi</c> 字段可能缺失或为 0，而 0 做分母会得到
    /// 无穷大，窗口尺寸直接变成 <c>NaN</c> 或天文数字。§13.8 明确规定「savedDpi 为 0 或缺失时视为 96」。
    /// </remarks>
    public static uint Normalize(uint dpi) => dpi == 0 ? DefaultDpi : dpi;

    /// <summary>
    /// 计算「保存时尺寸」在当前显示器上应有的缩放系数：<c>currentDpi / savedDpi</c>。
    /// </summary>
    /// <param name="savedDpi">保存布局当时的 DPI，0 视为 96。</param>
    /// <param name="currentDpi">目标显示器当前的 DPI，0 视为 96。</param>
    /// <returns>缩放系数。同 DPI 时为 1，150% 屏上存、100% 屏上看时为 0.667。</returns>
    public static double Scale(uint savedDpi, uint currentDpi) =>
        (double)Normalize(currentDpi) / Normalize(savedDpi);

    /// <summary>
    /// 计算「界面常量（DIP）→ 物理像素」的缩放系数：<c>currentDpi / 96</c>。
    /// </summary>
    /// <param name="currentDpi">目标显示器当前的 DPI，0 视为 96。</param>
    public static double DipToPixelScale(uint currentDpi) => (double)Normalize(currentDpi) / DefaultDpi;

    /// <summary>
    /// 按系数缩放宽高。<strong>位置不动</strong>：位置由夹取算法决定，缩放只该改变尺寸。
    /// </summary>
    public static PixelRect ScaleSize(PixelRect rect, double scale) =>
        new(rect.X, rect.Y, rect.Width * scale, rect.Height * scale);

    /// <summary>
    /// 从 WPF 的 <c>CompositionTarget.TransformToDevice</c> 的 <c>M11</c> 反推 DPI。
    /// </summary>
    /// <param name="m11">
    /// DIP 到物理像素的横向缩放比例：100% 时为 1.0、150% 时为 1.5、200% 时为 2.0。
    /// </param>
    /// <returns>四舍五入到整数的 DPI：96 / 144 / 192。</returns>
    /// <remarks>
    /// <c>M11</c> 是<strong>比例</strong>不是百分比，所以是 <c>96 * m11</c> 而不是 <c>96 * m11 / 100</c>。
    /// 传入 0 或负数（尚未接上渲染源时会读到 0）时返回 <see cref="DefaultDpi"/>，
    /// 绝不能让这个值流进除法。
    /// </remarks>
    public static uint DpiFromScale(double m11)
    {
        if (m11 <= 0 || double.IsNaN(m11) || double.IsInfinity(m11))
        {
            return DefaultDpi;
        }

        return (uint)Round(DefaultDpi * m11);
    }
}
