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
/// 定位二选一（Phase 1 设计 §6.3）：<see cref="Anchor"/> 非空时，排版期由锚点推导
/// <see cref="Rect"/>（两遍排版）；否则用调用方直接给的 <see cref="Rect"/>（S2 现状路径，
/// 编辑器拖动图片时逐帧更新）。
/// </remarks>
public sealed record FloatObject(int Id, LayoutRect Rect, FloatSide Side, float Margin = 0f)
{
    /// <summary>锚点定位；<see langword="null"/> = 矩形直给路径。</summary>
    public FloatAnchor? Anchor { get; init; }

    /// <summary>返回矩形平移后的副本（拖动场景）。</summary>
    public FloatObject MovedTo(float x, float y) => this with { Rect = Rect with { X = x, Y = y } };
}

/// <summary>
/// 浮动锚点：锚定到块首 + 偏移（O4，与 Word「随文字移动」同构）。
/// 字符级锚定不做（v2 评审简化，原 CharIndex 字段已从契约删除，未来需要时升 schema 加回）。
/// </summary>
/// <remarks>三字段恒写出（JsonIgnore(Never)）：0 是锚点的主流值（块 0、零偏移），
/// 不能因为「恰好是默认值」就从契约里消失。</remarks>
public sealed record FloatAnchor(
    [property: JsonPropertyName("block"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int BlockIndex,
    [property: JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] float OffsetX,
    [property: JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] float OffsetY);
