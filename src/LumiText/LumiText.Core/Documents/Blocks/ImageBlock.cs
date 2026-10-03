using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 图片块：<see cref="ImageId"/> 引用 <see cref="Document"/> 图片表中的位图资源，
/// 显示尺寸（dip）存在模型里（排版不依赖解码结果，Phase 1 设计 §8.1）。
/// 带 <see cref="Float"/> 时经 <see cref="Document.GetFloats"/> 派生为引擎的浮动输入；
/// 不带时排版为「占位行盒」（PlacedLine.Kind = ImagePlaceholder）。
/// </summary>
public sealed record ImageBlock(
    [property: JsonPropertyName("imageId")] string ImageId,
    [property: JsonPropertyName("width")] float Width,
    [property: JsonPropertyName("height")] float Height,
    [property: JsonPropertyName("float")] FloatPlacement? Float = null) : Block;

/// <summary>
/// 浮动参数。锚定与直给二选一：<see cref="Anchor"/> 非空时排版期由锚点推导矩形（§6.3 两遍排版）；
/// 否则用 <see cref="Position"/> 直给（缺省视为 (0,0)，与 S2 现状路径一致）。
/// <see cref="AnchorToChar"/>（Phase 3 M4，缺省 false 不写出）区分锚定后的横向落位：
/// true = 紧跟在锚字符之后（拖放落点语义，X 由锚字符的行内位置决定，随文字重排一起走）；
/// false = 按 <see cref="Side"/> 贴左/右缘（粘贴插图默认）。
/// </summary>
public sealed record FloatPlacement(
    [property: JsonPropertyName("side")] FloatSide Side,
    [property: JsonPropertyName("margin")] float Margin = 0f,
    [property: JsonPropertyName("anchor")] FloatAnchor? Anchor = null,
    [property: JsonPropertyName("position")] FloatPosition? Position = null,
    [property: JsonPropertyName("anchorChar")] bool AnchorToChar = false);

/// <summary>浮动矩形左上角（文档坐标 dip；宽高取自 <see cref="ImageBlock"/> 显示尺寸，不重复存储）。
/// 与 <see cref="FloatAnchor"/> 同理，坐标恒写出。</summary>
public readonly record struct FloatPosition(
    [property: JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] float X,
    [property: JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] float Y);
