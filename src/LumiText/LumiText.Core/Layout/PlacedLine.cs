namespace LumiText.Core.Layout;

/// <summary>行盒种类（渲染层分派绘制路径的依据，Phase 1 设计 §4）。</summary>
public enum PlacedLineKind
{
    /// <summary>普通文本行（Paragraph/Heading 块）。</summary>
    Text,

    /// <summary>Todo 块文本行（渲染层在块首行的缩进区画矢量复选框，§6.2）。</summary>
    TodoText,

    /// <summary>Bullet 段落文本行（渲染层在块首行的缩进区画实心圆点，§0.1 分点基线）。</summary>
    BulletText,

    /// <summary>分割线占位行盒（无文本，渲染层画 1px 水平线）。</summary>
    Divider,

    /// <summary>图片占位行盒（预留；M4 的 ImageBlock 走浮动通道，不产生占位行盒）。</summary>
    ImagePlaceholder,
}

/// <summary>
/// 一个已放置的行盒：一段连续文本在某个段（Segment）内的最终位置。
/// 同行盒被浮动对象挤到图片两侧时，同一"视觉行"会产生多个 <see cref="PlacedLine"/>，
/// 它们共享同一 <see cref="Baseline"/>。
/// </summary>
/// <param name="BlockIndex">所属块在文档块列表中的序号（M4 由 ParagraphIndex 改名——输入已是块列表）。</param>
/// <param name="CharStart">行内首字符在块文本中的偏移。</param>
/// <param name="CharCount">行内容纳的字符数。</param>
/// <param name="X">行盒左上角 X（dip）。</param>
/// <param name="Y">行盒左上角 Y（dip，文档坐标）。</param>
/// <param name="Width">行实际占用宽度。</param>
/// <param name="Height">行盒高度（Ascent + Descent）。</param>
/// <param name="Baseline">基线的 Y 坐标（文档坐标）。</param>
/// <param name="Batch">本行所属的批量行（M3）；渲染层据此按批分组绘制（Phase 1 设计 §7.3）。</param>
/// <param name="LineOffsetY">本行顶缘相对批布局顶缘的偏移（批内定位，取自 <see cref="MeasuredLine.OffsetY"/>）。</param>
/// <param name="BatchStart">本行所属批的文本流起点在<b>块文本</b>中的偏移：批的文本 = 块文本从
/// <see cref="BatchStart"/> 起的切片（段一变——绕图、缩进变化——引擎就按剩余文本重建批），
/// 因此「批内偏移 + <see cref="BatchStart"/> = 块内偏移」。命中/光标/选区几何全按这条换算
/// （Phase 3 M4 修复：漏了它，绕图段落的点击会往段落前面偏）。无批占位行盒为 0。</param>
/// <param name="Kind">行盒种类。</param>
/// <param name="IsBlockStart">是否所属块的第一个行盒（Todo 复选框只画在首行，§6.2）。</param>
public sealed record PlacedLine(
    int BlockIndex,
    int CharStart,
    int CharCount,
    float X,
    float Y,
    float Width,
    float Height,
    float Baseline,
    ILineBatch? Batch,
    float LineOffsetY,
    int BatchStart,
    PlacedLineKind Kind = PlacedLineKind.Text,
    bool IsBlockStart = false)
{
    public LayoutRect Bounds => new(X, Y, Width, Height);
}
