namespace LumiMemo.Core.Models;

/// <summary>
/// 某一台显示器在<strong>本次枚举那一刻</strong>的状态快照（§13.8）。
/// </summary>
/// <remarks>
/// <para>
/// 是一次性的快照而不是活对象：显示器热插拔会换掉整份列表，拿着旧快照去读是错的。
/// 每次需要显示器信息时都重新问一次 <c>IDisplayProvider</c>。
/// </para>
/// <para>
/// <see cref="Dpi"/> 是这台显示器<strong>当前</strong>的 DPI，会随用户改系统缩放而变；
/// 它与 <see cref="NoteLayout.Dpi"/>（保存布局<strong>当时</strong>的历史快照）是两件事，
/// 不要混用。缩放只依赖后者（§13.8）。
/// </para>
/// </remarks>
/// <param name="DeviceId">
/// 显示器的设备路径，跨会话稳定（§13.8）。
/// </param>
/// <param name="FriendlyName">人类可读的名字，仅用于日志与「设置」里展示。</param>
/// <param name="BoundsPx">整块屏幕的矩形，含任务栏所占的区域。</param>
/// <param name="WorkAreaPx">工作区矩形，<strong>不含</strong>任务栏与贴靠区域。夹取以它为准。</param>
/// <param name="Dpi">当前 DPI：96 / 120 / 144 / 192 等。</param>
public sealed record DisplaySnapshot(
    string DeviceId,
    string FriendlyName,
    PixelRect BoundsPx,
    PixelRect WorkAreaPx,
    uint Dpi)
{
    /// <summary>
    /// 是否是主显示器。显示器消失后要把窗口层叠回主显示器，因此这个标记是必需品（§13.8）。
    /// </summary>
    public bool IsPrimary { get; init; }
}
