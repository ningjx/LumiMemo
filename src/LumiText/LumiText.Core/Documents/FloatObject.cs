using LumiText.Core.Layout;

namespace LumiText.Core.Documents;

/// <summary>浮动方向。当前支持左/右浮动；居中浮动预留枚举位，排版时按右浮动处理。</summary>
public enum FloatSide
{
    Left,
    Right,
    Center,
}

/// <summary>
/// 浮动对象（图片的排版抽象）：文档坐标系中的一个矩形 + 外边距。
/// 文字围绕 <see cref="Rect"/> 按 <see cref="Margin"/> 外扩后的排除区流动。
/// </summary>
/// <remarks>
/// 当前迭代由调用方直接给定矩形（编辑器拖动图片时逐帧更新）。
/// Phase 1 将引入"锚定到段落字符"的浮动定位，矩形由锚点推导。
/// </remarks>
public sealed record FloatObject(int Id, LayoutRect Rect, FloatSide Side, float Margin = 0f)
{
    /// <summary>返回矩形平移后的副本（拖动场景）。</summary>
    public FloatObject MovedTo(float x, float y) => this with { Rect = Rect with { X = x, Y = y } };
}
