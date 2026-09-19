namespace LumiMemo.Core.Models;

/// <summary>
/// 一张便签的<strong>设备状态</strong>：窗口坐标、折叠、置顶、缩放等「只对当前这台电脑有意义」的状态（§9.2）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Note"/> 严格分离：本类型只写 <c>%LOCALAPPDATA%\LumiMemo\layout.json</c>，
/// 绝不写进 Markdown 的 Front Matter。理由见 §8.1——把 A 电脑的窗口坐标同步到 B 电脑，
/// 结果是窗口跑到屏幕外。
/// </para>
/// <para>
/// <strong>所有坐标与尺寸都是物理像素，不是 DIP</strong>（§8.3）。这样与 Win32 的
/// <c>SetWindowPos</c>、<c>GetWindowRect</c>、显示器工作区保持同一单位，中间不需要换算。
/// </para>
/// </remarks>
public sealed class NoteLayout
{
    /// <summary>对应的便签 id。<c>layout.json</c> 按 id 索引，因此便签被移动或重命名后仍能找到自己的布局。</summary>
    public required Guid NoteId { get; init; }

    /// <summary>退出时这张便签是否是打开的。启动时据此恢复窗口（§8.3）。</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>
    /// 所在显示器的设备路径，跨会话稳定（§13.8）。
    /// </summary>
    /// <remarks>
    /// 用设备路径而不是 <c>\\.\DISPLAY1</c>：后者在热插拔或重启后可能被重新分配。
    /// <c>null</c> 表示尚未确定（例如首次出现的便签）。
    /// </remarks>
    public string? DisplayId { get; set; }

    // 以下坐标与尺寸全部是「物理像素」，不是 DIP（§8.3）

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 360;

    public double Height { get; set; } = 420;

    /// <summary>
    /// 保存这份布局时目标显示器的 DPI（96 / 120 / 144 / 192 …）。
    /// 恢复时按 <c>currentDpi / Dpi</c> 缩放物理尺寸，保证跨 DPI 显示器后外观大小一致（§13.8）。
    /// </summary>
    /// <remarks>
    /// 这是「保存当时」的历史快照，与 <c>layout.json</c> 的 <c>displays[displayId].dpi</c>
    /// （每台显示器<strong>当前</strong>的 DPI）是两件事，不要混用。窗口摆位只依赖本字段（§9.2）。
    /// </remarks>
    public uint Dpi { get; set; } = 96;

    /// <summary>
    /// 展开状态下的高度。折叠时 <see cref="Height"/> 变成标题条高度，展开时恢复为此值（§15.2）。
    /// </summary>
    /// <remarks>
    /// 折叠时如果直接改 <see cref="Height"/>，展开就不知道原高度；如果改 Height 又不记录，
    /// 窗口真实尺寸与视觉尺寸不一致，会污染工作区夹取与显示器判定（§8.3）。
    /// </remarks>
    public double ExpandedHeight { get; set; } = 420;

    public bool IsCollapsed { get; set; }

    public bool IsTopMost { get; set; }

    public bool IsLocked { get; set; }
}
