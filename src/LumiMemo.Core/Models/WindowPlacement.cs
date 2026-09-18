namespace LumiMemo.Core.Models;

/// <summary>
/// 一次「便签该摆在哪」的计算结果（§13.8）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="NoteLayout"/> 的区别：<see cref="NoteLayout"/> 是<strong>存进磁盘的原始数据</strong>，
/// 本类型是<strong>算完之后交给 Win32 的最终答案</strong>。两者的坐标单位都是物理像素，
/// 所以换算过程里没有任何单位转换。
/// </para>
/// <para>
/// 不直接在 <see cref="NoteLayout"/> 上改写是因为：原始数据要保留下来，用户把显示器插回去之后
/// 才能恢复原样。若在恢复时就地覆盖，拔一次显示器就等于永久丢掉了原来的坐标。
/// </para>
/// </remarks>
/// <param name="DisplayId">目标显示器的设备路径，用于回写到 <see cref="NoteLayout.DisplayId"/>。</param>
/// <param name="Bounds">最终位置与尺寸，物理像素，已按当前 DPI 缩放并夹取过。</param>
/// <param name="Dpi">目标显示器当前的 DPI，写回 <see cref="NoteLayout.Dpi"/> 时用。</param>
/// <param name="WasAdjusted">
/// 是否被修正过（缩放、夹取、或原显示器已不存在）。为 true 时调用方要写一条日志，
/// 否则用户会莫名其妙「便签怎么跑那儿去了」（§13.8）。
/// </param>
public readonly record struct WindowPlacement(
    string DisplayId,
    PixelRect Bounds,
    uint Dpi,
    bool WasAdjusted);
