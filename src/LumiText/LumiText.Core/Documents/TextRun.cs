using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 一段带行内样式的文本。块内全部 run 的 <see cref="Text"/> 拼接即块文本；
/// 文本内不允许含 \r / \n（换行即分段，沿用段落契约）。
/// </summary>
public sealed record TextRun(
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("s")] InlineStyle? Style = null);
