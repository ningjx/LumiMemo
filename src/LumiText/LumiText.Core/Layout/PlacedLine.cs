namespace LumiText.Core.Layout;

/// <summary>
/// 一个已放置的行盒：一段连续文本在某个段（Segment）内的最终位置。
/// 同行盒被浮动对象挤到图片两侧时，同一"视觉行"会产生多个 <see cref="PlacedLine"/>，
/// 它们共享同一 <see cref="Baseline"/>。
/// </summary>
/// <param name="ParagraphIndex">所属段落在文档中的序号。</param>
/// <param name="CharStart">行内首字符在段落文本中的偏移。</param>
/// <param name="CharCount">行内容纳的字符数。</param>
/// <param name="X">行盒左上角 X（dip）。</param>
/// <param name="Y">行盒左上角 Y（dip，文档坐标）。</param>
/// <param name="Width">行实际占用宽度。</param>
/// <param name="Height">行盒高度（Ascent + Descent）。</param>
/// <param name="Baseline">基线的 Y 坐标（文档坐标）。</param>
/// <param name="NativeLayout">度量器私有布局产物（可选），供渲染层直接使用。</param>
public sealed record PlacedLine(
    int ParagraphIndex,
    int CharStart,
    int CharCount,
    float X,
    float Y,
    float Width,
    float Height,
    float Baseline,
    object? NativeLayout)
{
    public LayoutRect Bounds => new(X, Y, Width, Height);
}
