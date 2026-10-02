using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>一次"首行探测"的结果：以给定宽度排版后，第一行能容纳多少字符及其度量。</summary>
/// <param name="CharsConsumed">第一行容纳的字符数；0 表示该宽度下一个字符都放不下（窄段放弃信号）。</param>
/// <param name="Width">第一行实际占用宽度。</param>
/// <param name="Ascent">第一行基线以上的高度。</param>
/// <param name="Descent">第一行基线以下的高度。</param>
/// <param name="NativeLayout">
/// 度量器私有的布局产物（如 Win2D 的 CanvasTextLayout），原样透传给渲染层，
/// 排版引擎不解读、不缓存；由 <see cref="LayoutResult"/> 统一负责释放。
/// </param>
public readonly record struct FirstLineInfo(
    int CharsConsumed, float Width, float Ascent, float Descent, object? NativeLayout);

/// <summary>指定样式下空行的行高度量（用于空段落占位）。</summary>
public readonly record struct LineHeightInfo(float Ascent, float Descent)
{
    public float Total => Ascent + Descent;
}

/// <summary>
/// 文本度量抽象：排版引擎与具体文本栈（Win2D/DirectWrite）之间的唯一耦合点。
/// 单元测试注入等宽假字体实现，使环绕行为确定性可断言（无需 GPU/窗口）。
/// </summary>
/// <remarks>
/// 实现方约定：
/// <list type="bullet">
/// <item><see cref="LayoutFirstLine"/> 返回的 <see cref="FirstLineInfo.NativeLayout"/>
/// 所有权随返回值转移给调用方；<c>CharsConsumed = 0</c> 时实现方必须已自行清理。</item>
/// <item>度量单位与排版引擎一致：dip（设备无关像素）。</item>
/// </list>
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>以 <paramref name="maxWidth"/> 排版 <paramref name="text"/>，只报告第一行。</summary>
    FirstLineInfo LayoutFirstLine(string text, TextStyle style, float maxWidth);

    /// <summary>测量指定样式下一个普通行的高度（用于空段落占位）。</summary>
    LineHeightInfo MeasureLineHeight(TextStyle style);
}
