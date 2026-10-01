namespace LumiMemo.WinUI.Controls;

/// <summary>标题行的布局计算（给 XAML 的 x:Bind 函数绑定用）。</summary>
public static class TitleLayout
{
    /// <summary>
    /// 标题可用的最大宽度：标题行总宽 − 右侧图标组 − 固定预留。
    /// 预留 = 左右内边距 21（14+7）+ 进度圈 16 与左边距 6 + 刷新按钮 26 与左 6/右 3。
    /// </summary>
    /// <remarks>
    /// 标题在 MaxWidth 处裁剪省略号：短标题时标题区自然收窄、刷新按钮紧贴标题；
    /// 长标题时标题吃满预留后的宽度、刷新按钮被顶到最右，其右缘距右侧图标组 3——
    /// 与图标组内部的 <c>Spacing</c> 一致。
    /// </remarks>
    public static double TitleMaxWidth(double titleBarWidth, double toolsWidth) =>
        Math.Max(0, titleBarWidth - toolsWidth - 78);
}
