using System.Text.Json.Serialization;
using LumiText.Core.Layout;

namespace LumiText.Core.Documents;

/// <summary>浮动方向。当前支持左/右浮动；居中浮动预留枚举位，排版时按右浮动处理。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FloatSide>))]
public enum FloatSide
{
    [JsonStringEnumMemberName("left")]
    Left,
    [JsonStringEnumMemberName("right")]
    Right,
    [JsonStringEnumMemberName("center")]
    Center,
}

/// <summary>
/// 浮动对象（图片的排版抽象）：文档坐标系中的一个矩形 + 外边距。
/// 文字围绕 <see cref="Rect"/> 按 <see cref="Margin"/> 外扩后的排除区流动。
/// </summary>
/// <remarks>
/// 定位二选一（字符级锚定补丁，2026-10-03）：<see cref="Anchor"/> 非空时，排版期由锚点
/// 推导 <see cref="Rect"/>（两遍排版）；否则用调用方直接给的 <see cref="Rect"/>
/// （编辑器拖动图片时逐帧预览走这条路径，松手后把落点字符写回 <see cref="Anchor"/>）。
/// </remarks>
public sealed record FloatObject(int Id, LayoutRect Rect, FloatSide Side, float Margin = 0f)
{
    /// <summary>锚点定位；<see langword="null"/> = 矩形直给路径。</summary>
    public FloatAnchor? Anchor { get; init; }

    /// <summary>
    /// 锚定后的横向落位是否「紧跟锚字符之后」（Phase 3 M4 拖放落点语义）：
    /// true → X 由锚字符的行内位置决定（随文字重排一起走）；
    /// false → 按 <see cref="Side"/> 贴左/右缘。
    /// </summary>
    public bool AnchorToChar { get; init; }

    /// <summary>返回矩形平移后的副本（拖动场景）。</summary>
    public FloatObject MovedTo(float x, float y) => this with { Rect = Rect with { X = x, Y = y } };
}

/// <summary>
/// 浮动锚点：锚定到块内某个字符前面（字符级，2026-10-03 补丁取代块级锚定）。
/// </summary>
/// <remarks>
/// <para>
/// 语义：图片位置 = 锚字符<b>所在行的行盒顶缘</b>（Y），水平方向按 <see cref="FloatSide"/>
/// 贴内容区左/右缘（X 与锚字符的行内偏移无关——与 Word「随文字移动 + 左右对齐」同构）。
/// </para>
/// <para>
/// <see cref="CharIndex"/> 是<b>块内字符偏移</b>：与该块 <c>Runs</c> 拼接后的文本流对齐，
/// 与 Phase 2 编辑层的 <c>TextPosition.CharIndex</c> 同坐标系——编辑器拖动图片落下时，
/// 把落点的 <c>TextPosition</c> 直接存为 <see cref="FloatAnchor"/>，无需换算。
/// </para>
/// <para>
/// 越界处理：<see cref="CharIndex"/> ≥ 块总字符数 → 钳到该块末行；<see cref="BlockIndex"/>
/// 越界或锚到非文本块 → 顺延/钳制路径与块级锚定一致（FlowLayoutEngine.ResolveAnchoredFloats）。
/// </para>
/// <para>
/// 序列化：两字段恒写出（JsonIgnore(Never)）：0 是锚点的主流值（块 0、字符 0），
/// 不能因为「恰好是默认值」就从契约里消失。schema 演进：v1 的 <c>{"block","x","y"}</c>
/// 由 STJ 未知字段忽略 + 缺省降级天然读为 <c>{"block":N,"char":0}</c>，无需迁移代码。
/// </para>
/// </remarks>
public sealed record FloatAnchor(
    [property: JsonPropertyName("block"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int BlockIndex,
    [property: JsonPropertyName("char"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int CharIndex);
