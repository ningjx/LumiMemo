using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>指定样式下空行的行高度量（用于空段落占位）。</summary>
public readonly record struct LineHeightInfo(float Ascent, float Descent)
{
    public float Total => Ascent + Descent;
}

/// <summary>批量行内一行的度量。</summary>
/// <param name="CharStart">本行在「本批文本流」中的起始偏移（UTF-16 code unit）。</param>
/// <param name="CharsConsumed">本行字符数；0 表示该宽度下一个字符都放不下（窄段放弃信号）。</param>
/// <param name="Width">行推进宽度（末字符后插入符 X）。</param>
/// <param name="Ascent">基线以上高度（含行内大字撑高，DirectWrite 标准行为）。</param>
/// <param name="Descent">基线以下高度。</param>
/// <param name="OffsetY">本行顶缘相对批布局顶缘的偏移（渲染按批分组定位用，Phase 1 设计 §7.3）。</param>
public readonly record struct MeasuredLine(
    int CharStart, int CharsConsumed, float Width,
    float Ascent, float Descent, float OffsetY);

/// <summary>字符级命中结果（M2 编辑层）：命中的字符偏移 + 是否落尾。</summary>
/// <param name="CharacterIndex">命中字符在批文本流中的偏移。</param>
/// <param name="IsTrailingHit">true = 光标落在该字符之后；false = 之前。</param>
public readonly record struct CharHit(int CharacterIndex, bool IsTrailingHit);

/// <summary>一段连续字符的几何区域（选区高亮用）。</summary>
/// <param name="CharacterIndex">段首字符在批文本流中的偏移。</param>
/// <param name="CharacterCount">段字符数。</param>
/// <param name="X">区域左上角 X（批布局坐标）。</param>
/// <param name="Y">区域左上角 Y（批布局坐标）。</param>
/// <param name="Width">区域宽。</param>
/// <param name="Height">区域高。</param>
public readonly record struct CharRegion(
    int CharacterIndex, int CharacterCount,
    float X, float Y, float Width, float Height);

/// <summary>
/// 一批行：同一段（X，宽）下对一段文本流一次排版得到的全部行。
/// 归调用方（排版引擎）持有，其生命周期内所有引用本批的 <see cref="PlacedLine"/> 都有效；
/// 引擎在弃批（段切换/交集重探）或 <see cref="LayoutResult.Dispose"/> 时统一释放。
/// </summary>
public interface ILineBatch : IDisposable
{
    /// <summary>批内行数（0 = 该宽度下一行都放不下，窄段放弃信号；调用方须立即 Dispose）。</summary>
    int LineCount { get; }

    /// <summary>第 <paramref name="index"/> 行的度量。</summary>
    MeasuredLine GetLine(int index);

    /// <summary>整批共享的度量器私有布局产物（如 Win2D 的 CanvasTextLayout），原样透传给渲染层。</summary>
    object? NativeLayout { get; }

    /// <summary>
    /// 字符级命中（M2）：以批布局坐标 (x, y) 命中字符。
    /// 未命中任何字符（点在文本区外）时返回 null。
    /// </summary>
    CharHit? HitTestChar(float x, float y);

    /// <summary>
    /// 光标几何（M2）：characterIndex 字符前/后的插入符位置（批布局坐标）。
    /// 返回 (x, yTop, height)：yTop 是光标顶缘 Y，height 是光标高度（随行高）。
    /// characterIndex 越界时钳到 [0, 文本长度]。
    /// </summary>
    (float X, float YTop, float Height) GetCaretGeometry(int characterIndex, bool isTrailing);

    /// <summary>
    /// 选区几何（M2）：[characterIndex, characterIndex+count) 的几何区域序列
    /// （每行一个矩形，自动处理跨行拆分与双向文本）。坐标为批布局坐标。
    /// </summary>
    IReadOnlyList<CharRegion> GetCharRegions(int characterIndex, int characterCount);
}

/// <summary>
/// 文本度量抽象：排版引擎与具体文本栈（Win2D/DirectWrite）之间的唯一耦合点。
/// 单元测试注入等宽假字体实现，使环绕行为确定性可断言（无需 GPU、无字体差异）。
/// </summary>
/// <remarks>
/// <para>M3 改造（Phase 1 设计 §5）：首行探测 <c>LayoutFirstLine</c> → 批量取行
/// <see cref="LayoutLines"/>——一个段（X，宽）只建一次布局、一次读回该宽度下的全部行，
/// 消除逐行重建布局的 O(字符数²/行高) 成本（S2 验收 [A] 超预算 9.4× 的解药）。</para>
/// <para>实现方约定：返回的批所有权随返回值转移给调用方；度量单位与排版引擎一致（dip）。</para>
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>以 <paramref name="maxWidth"/> 对整条文本流一次排版，返回全部行（批量接口）。</summary>
    /// <param name="runs">样式 run 序列（拼接即文本流）；实现方须在读取行度量之前应用行内样式
    /// （Set* 会触发重排，先读后设 = 拿到无样式旧度量 + 白排一次——M1-U2 实证）。</param>
    ILineBatch LayoutLines(IReadOnlyList<TextRun> runs, TextStyle baseStyle, float maxWidth);

    /// <summary>测量指定样式下一个普通行的高度（用于空段落占位）。</summary>
    LineHeightInfo MeasureLineHeight(TextStyle style);
}
