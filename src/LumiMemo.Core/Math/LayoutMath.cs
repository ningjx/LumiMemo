// 与 DpiMath.cs 同样的理由：本命名空间就叫 LumiMemo.Core.Math，
// 只能用 using static 导入成员名，不能导入 Math 这个名字。
using static System.Math;

using LumiMemo.Core.Models;

namespace LumiMemo.Core.Math;

/// <summary>
/// 窗口摆位的全部算法：缩放、夹取、层叠、折叠高度（§13.8、§15.1、§15.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>这是坐标规则的唯一实现处</strong>（§8.3）。<c>LayoutService</c>、<c>WindowManager</c>、
/// <c>JsonLayoutStore</c> 都不许再写一份「窗口该摆哪」的判断，只能调这里。
/// 两份实现必然会在某个边界条件上分歧，而那种分歧表现为「便签偶尔跑到屏幕外」，
/// 极难复现也极难定位。
/// </para>
/// <para>
/// 全部输入输出都是<strong>物理像素</strong>，只有 <see cref="CollapsedHeight"/> 的入参和返回值
/// 是 DIP（它是界面常量，不是用户拖出来的尺寸），换算在 <see cref="Restore"/> 内部完成。
/// </para>
/// </remarks>
public static class LayoutMath
{
    // ---- §15.1 尺寸常量（DIP） ----

    /// <summary>标题条高度，36 DIP（§15.1）。折叠态只剩它。</summary>
    public const double TitleBarHeight = 36;

    /// <summary>状态条高度，24 DIP（§15.2）。可整体隐藏。</summary>
    public const double StatusBarHeight = 24;

    /// <summary>便签最小宽度，240 DIP（§15.1）。再窄标题会被截断到无法辨认。</summary>
    public const double MinimumWidth = 240;

    /// <summary>便签展开时的最小高度，120 DIP（§15.1）。</summary>
    public const double MinimumExpandedHeight = 120;

    // ---- 折叠高度 ----

    /// <summary>折叠后带状态条的高度，44 DIP（§15.1、§15.2）。</summary>
    public const double CollapsedHeightWithStatusBar = 44;

    /// <summary>折叠后不带状态条的高度，即一个标题条的净高，36 DIP。</summary>
    public const double CollapsedHeightWithoutStatusBar = TitleBarHeight;

    // ---- 夹取常量（物理像素，§13.8） ----

    /// <summary>横向至少要有这么多像素的标题条留在工作区内，否则就把窗口拉回来。</summary>
    public const double HorizontalPeekPx = 80;

    /// <summary>纵向至少要留 40px，保证标题条不会顶在工作区下边缘之下。</summary>
    public const double VerticalPeekPx = 40;

    // ---- 层叠常量（物理像素，§13.8） ----

    /// <summary>层叠第 1 张的偏移。</summary>
    public const double CascadeOriginOffset = 24;

    /// <summary>层叠每张之间的递增步长。</summary>
    public const double CascadeStep = 28;

    /// <summary>层叠到第几张后回到起点。</summary>
    public const int CascadeSlots = 6;

    /// <summary>每绕一圈额外增加的偏移，避免第二轮完全盖住第一轮。</summary>
    public const double CascadeWrapOffset = 8;

    /// <summary>判定「被修正过」时的浮点容差。</summary>
    private const double Epsilon = 0.01;

    /// <summary>
    /// 折叠状态下的窗口高度（DIP）。
    /// </summary>
    /// <param name="showStatusBar">
    /// 是否显示状态条。<see langword="true"/> 时给状态条留出位置。
    /// </param>
    /// <remarks>
    /// 文档 §15.2 说折叠态「只剩标题条 + 状态条，高度约 44 DIP」，但同一节给的标题条是 36 DIP、
    /// 状态条是 24 DIP，两者相加是 60。三处数字对不上，这是文档自身的笔误。
    /// 这里以<strong>两处都写明的 44</strong> 为准（§15.1 也把它作为折叠最小高度），
    /// 即折叠态的状态条被压进标题条那一行里；关掉状态条则退回纯标题条的 36。
    /// </remarks>
    public static double CollapsedHeight(bool showStatusBar) =>
        showStatusBar ? CollapsedHeightWithStatusBar : CollapsedHeightWithoutStatusBar;

    /// <summary>
    /// 找到包含指定物理像素点的显示器（§13.8 第 1 步）。
    /// </summary>
    /// <param name="displays">当前所有显示器。</param>
    /// <param name="xPx">物理像素横坐标，副屏在主屏左侧时是负数。</param>
    /// <param name="yPx">物理像素纵坐标。</param>
    /// <returns>包含该点的显示器；一个都不包含时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 用<strong>整块屏幕</strong>的矩形（<see cref="DisplaySnapshot.BoundsPx"/>）而不是工作区：
    /// 窗口压在任务栏那一带时它仍然在那台显示器上，不该被判成「显示器不存在」。
    /// 工作区只用于夹取。
    /// </remarks>
    public static DisplaySnapshot? FindDisplayContaining(
        IReadOnlyList<DisplaySnapshot> displays,
        double xPx,
        double yPx)
    {
        ArgumentNullException.ThrowIfNull(displays);

        foreach (DisplaySnapshot display in displays)
        {
            PixelRect bounds = display.BoundsPx;

            // 左闭右开：相邻两台显示器的接缝上只有一个点，闭区间会让它同时命中两台。
            if (xPx >= bounds.X && xPx < bounds.Right && yPx >= bounds.Y && yPx < bounds.Bottom)
            {
                return display;
            }
        }

        return null;
    }

    /// <summary>
    /// 把窗口夹回工作区内（§13.8「工作区夹取」）。
    /// </summary>
    /// <param name="rect">期望的位置与尺寸，物理像素。</param>
    /// <param name="workArea">目标显示器的工作区（不含任务栏）。</param>
    /// <param name="minimumWidthPx">最小宽度，物理像素。用 <see cref="MinimumSizeInPixels"/> 算。</param>
    /// <param name="minimumHeightPx">最小高度，物理像素。</param>
    /// <returns>修正后的位置与尺寸。</returns>
    /// <remarks>
    /// <para>
    /// 两条设计意图，改这些常量前先读一遍：
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     尺寸超工作区时<strong>缩到工作区大小</strong>，而不是保持原尺寸把窗口挂在屏幕外。
    ///     副屏换成竖屏后，横着放不下的便签必须还能用。
    ///   </item>
    ///   <item>
    ///     位置<strong>不强行居中</strong>，只保证横向 80px、纵向 40px 的「抓手」可见。
    ///     窗口允许有一部分悬在工作区外（用户可能就是故意的），
    ///     但只要标题条还能抓到，就永远不会出现「拖丢了找不回来」。
    ///   </item>
    /// </list>
    /// <para>
    /// 四条位置修正的<strong>顺序</strong>与文档一致，不要重排：第 1 条用的是缩放后的宽度，
    /// 与第 2 条配合才能保证「左边缘越界」和「右边缘越界」不会互相覆盖。
    /// </para>
    /// <para>
    /// <strong>最小尺寸由调用方传进来，不在本函数里读常量。</strong> §15.1 的最小尺寸是 DIP，
    /// 而本函数全程是物理像素，换算要用目标显示器的 DPI——本函数不认识显示器。
    /// 而且最小高度随折叠状态而变（折叠态就是 44 或 36 DIP 本身，比展开态的 120 还小），
    /// 在此处写死一个只会把折叠态的窗口又撑回去。
    /// </para>
    /// <para>
    /// 先撑到最小值、再按工作区封顶，顺序不能反：最小尺寸在工作区放不下的小屏上
    /// （240 DIP 的窗口遇上更窄的工作区）应当以工作区为准，否则窗口反而会超出屏幕。
    /// </para>
    /// </remarks>
    public static PixelRect Clamp(
        PixelRect rect,
        PixelRect workArea,
        double minimumWidthPx,
        double minimumHeightPx)
    {
        double width = Min(Max(rect.Width, minimumWidthPx), workArea.Width);
        double height = Min(Max(rect.Height, minimumHeightPx), workArea.Height);

        double x = rect.X;
        double y = rect.Y;

        if (x + width < workArea.X + HorizontalPeekPx)
        {
            x = workArea.X;
        }

        if (x > workArea.Right - HorizontalPeekPx)
        {
            x = workArea.Right - width;
        }

        if (y < workArea.Y)
        {
            y = workArea.Y;
        }

        if (y > workArea.Bottom - VerticalPeekPx)
        {
            y = workArea.Bottom - height;
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// 层叠摆放：显示器拔掉后把便签逐张错开摊在工作区上（§13.8「显示器不存在时」）。
    /// </summary>
    /// <param name="workArea">目标显示器的工作区。</param>
    /// <param name="index">这张便签是本轮层叠里的第几张，从 0 开始。</param>
    /// <param name="width">窗口宽度，物理像素。</param>
    /// <param name="height">窗口高度，物理像素。</param>
    /// <returns>层叠位置，<strong>尚未夹取</strong>，调用方要再过一遍 <see cref="Clamp"/>。</returns>
    /// <remarks>
    /// 第 1 张偏 (24, 24)，此后每张多偏 28；满 6 张回到起点并再整体多偏 8。
    /// 不用「每张递增 28」无限增长是因为十几张之后偏移就大到把便签推出屏幕了。
    /// </remarks>
    public static PixelRect Cascade(PixelRect workArea, int index, double width, double height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        int slot = index % CascadeSlots;
        double wrap = index / CascadeSlots * CascadeWrapOffset;
        double offset = CascadeOriginOffset + (slot * CascadeStep) + wrap;

        return new PixelRect(workArea.X + offset, workArea.Y + offset, width, height);
    }

    /// <summary>
    /// 完整的恢复算法（§13.8）：定显示器 → 算缩放 → 换算尺寸 → 夹取。
    /// </summary>
    /// <param name="saved">磁盘上读到的布局。</param>
    /// <param name="displays">当前所有显示器。至少一台。</param>
    /// <param name="cascadeIndex">
    /// 本次启动中这是第几张被层叠重排的便签。<strong>只影响「原显示器已拔掉」这条路径</strong>，
    /// 正常情况下用不到。
    /// </param>
    /// <param name="showStatusBar">状态条是否显示，决定折叠态高度。</param>
    /// <returns>可以直接交给 <c>SetWindowPos</c> 的结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="displays"/> 是空列表。</exception>
    /// <remarks>
    /// <para>
    /// 返回值里的 <see cref="WindowPlacement.WasAdjusted"/> 是给日志用的（§13.8 明确要求记录这次修正）。
    /// 它同时覆盖三种情况：跨 DPI 缩放、被夹取、原显示器不存在。
    /// </para>
    /// <para>
    /// <strong>本方法不修改 <paramref name="saved"/>。</strong> 原始坐标必须留着，
    /// 用户把显示器插回来之后还要靠它恢复原样；就地覆盖等于永久丢失用户摆好的位置。
    /// </para>
    /// </remarks>
    public static WindowPlacement Restore(
        NoteLayout saved,
        IReadOnlyList<DisplaySnapshot> displays,
        int cascadeIndex,
        bool showStatusBar)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(displays);

        if (displays.Count == 0)
        {
            throw new ArgumentException("恢复布局至少需要一台显示器。", nameof(displays));
        }

        DisplaySnapshot primary = displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0];

        // §13.8 第 1 步：拿保存矩形的「中心点」去问它在哪台显示器上。
        // 用 Height 而不是 ExpandedHeight：折叠态的窗口真的只有一条高，
        // 拿展开高度算中心点会把一条贴在屏幕下沿的便签判到下面那台显示器去。
        DisplaySnapshot? target = FindDisplayContaining(
            displays,
            saved.X + (saved.Width / 2),
            saved.Y + (saved.Height / 2));

        if (target is null)
        {
            // 原显示器已拔掉。坐标此刻多半落在虚拟屏幕的「空洞」处，
            // 照搬过去的结果是窗口存在但看不见，用户只会觉得「便签丢了」。
            (double minWidth, double minHeight) = MinimumSizeInPixels(saved, primary.Dpi, showStatusBar);
            double cascadeWidth = saved.Width * DpiMath.Scale(saved.Dpi, primary.Dpi);
            double cascadeHeight = HeightInPixels(saved, primary.Dpi, showStatusBar);
            PixelRect cascaded = Cascade(primary.WorkAreaPx, cascadeIndex, cascadeWidth, cascadeHeight);

            return new WindowPlacement(
                primary.DeviceId,
                Clamp(cascaded, primary.WorkAreaPx, minWidth, minHeight),
                primary.Dpi,
                WasAdjusted: true);
        }

        double scale = DpiMath.Scale(saved.Dpi, target.Dpi);
        var desired = new PixelRect(
            saved.X,
            saved.Y,
            saved.Width * scale,
            HeightInPixels(saved, target.Dpi, showStatusBar));

        (double minimumWidth, double minimumHeight) = MinimumSizeInPixels(saved, target.Dpi, showStatusBar);
        PixelRect fitted = Clamp(desired, target.WorkAreaPx, minimumWidth, minimumHeight);

        // 缩放系数不是 1 也算「修正过」：位置尺寸都没变但视觉大小变了，
        // 用户同样会问「怎么变大了」，日志里应该有据可查。
        // 尺寸被撑到最小值同样算修正——`fitted != desired` 会覆盖到那条路径。
        bool adjusted = fitted != desired || Abs(scale - 1.0) > Epsilon;

        return new WindowPlacement(target.DeviceId, fitted, target.Dpi, adjusted);
    }

    /// <summary>
    /// 这张便签在指定显示器上的最小尺寸（物理像素，§15.1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的理由是<strong>手改坏的布局文件</strong>：<c>width: 0</c> 既不越界也不超工作区，
    /// 夹取的位置修正一条都不会命中，于是恢复出一个看不见、也抓不到的窗口——
    /// 而用户除了再手改一次 JSON 没有任何恢复途径。这是这份文件唯一能造成的「无法自救」的后果。
    /// </para>
    /// <para>
    /// 最小尺寸是 DIP 常量，必须按目标显示器的 DPI 换成物理像素。直接拿 240 当像素用的话，
    /// 在 150% 屏上只有 160 DIP，标题照旧被截断到认不出来，等于没兜住。
    /// </para>
    /// <para>
    /// 最小高度随状态而变：折叠态的高度本身就是常数（44 或 36 DIP），兜底值就是它自己；
    /// 只有展开态才用 §15.1 的 120 DIP。两条路径都不经过用户手改的那个值，
    /// 所以折叠态的高度天然不可能为 0。
    /// </para>
    /// </remarks>
    private static (double Width, double Height) MinimumSizeInPixels(
        NoteLayout saved,
        uint currentDpi,
        bool showStatusBar)
    {
        double dip = DpiMath.DipToPixelScale(currentDpi);

        return (
            MinimumWidth * dip,
            (saved.IsCollapsed ? CollapsedHeight(showStatusBar) : MinimumExpandedHeight) * dip);
    }

    /// <summary>
    /// 算出这张便签在目标显示器上应有的高度（物理像素）。
    /// </summary>
    /// <remarks>
    /// 展开态与折叠态的换算系数<strong>刻意不同</strong>：
    /// <list type="bullet">
    ///   <item>
    ///     展开高度是用户拖出来的物理像素，按「保存 DPI → 当前 DPI」的比值缩放，
    ///     这样换屏之后视觉大小不变。
    ///   </item>
    ///   <item>
    ///     折叠高度是界面常量（44 DIP），必须按「DIP → 当前显示器像素」换算。
    ///     若误用前者，在 150% 屏上折叠会得到 <c>44 × 1.5 = 66</c>，看着像是对的；
    ///     但从 150% 屏存、100% 屏看就变成 <c>44 × 0.667 = 29</c>，标题条直接被压扁。
    ///   </item>
    /// </list>
    /// </remarks>
    private static double HeightInPixels(NoteLayout saved, uint currentDpi, bool showStatusBar) =>
        saved.IsCollapsed
            ? CollapsedHeight(showStatusBar) * DpiMath.DipToPixelScale(currentDpi)
            : saved.ExpandedHeight * DpiMath.Scale(saved.Dpi, currentDpi);
}
