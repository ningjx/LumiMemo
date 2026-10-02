using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 段落级文本样式。v2 演进（Phase 1 设计 §3.2）：行内样式由 <see cref="InlineStyle"/> 承载
/// （粗/斜/删/下划/色/行内字号比），段落级仍是字体族 + 字号。
/// </summary>
public sealed record TextStyle(
    [property: JsonPropertyName("font")] string FontFamily,
    [property: JsonPropertyName("size")] float FontSize)
{
    /// <summary>库内默认样式：跟随系统的正文字体与 15dip 字号。</summary>
    public static TextStyle Default { get; } = new("Segoe UI", 15f);
}
