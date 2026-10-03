using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 段落级文本样式。v2 演进（Phase 1 设计 §3.2）：行内样式由 <see cref="InlineStyle"/> 承载
/// （粗/斜/删/下划/色/行内字号比），段落级是字体族 + 字号 + 粗体预设
/// （<see cref="Bold"/> 是 Phase 3 M2 追加：标题的段落级加粗；行内 <see cref="InlineStyle.Bold"/>
/// 仍可对段落内字符强制加粗）。
/// </summary>
public sealed record TextStyle(
    [property: JsonPropertyName("font")] string FontFamily,
    [property: JsonPropertyName("size")] float FontSize,
    [property: JsonPropertyName("bold")] bool Bold = false)
{
    /// <summary>
    /// 库内默认样式：跟随系统的正文字体与 14dip 字号。
    /// 字号经 M7 前置确认（Phase 1 设计 §9.2）：与现产品 RichEditBox 正文字号一致
    /// （产品未显式设置，取 WinUI 框架默认 14dip；初稿的 15dip 以此为准修正）。
    /// </summary>
    public static TextStyle Default { get; } = new("Segoe UI", 14f);
}
