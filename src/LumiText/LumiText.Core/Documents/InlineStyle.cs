using System.Text.Json.Serialization;
using LumiText.Core.Documents.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// ARGB 四字节颜色（Core 自持结构，避免 Core 依赖任何 UI 颜色类型；WinUI 侧单向转换）。
/// JSON 契约为 "#AARRGGBB" 字符串。
/// </summary>
public readonly record struct Color32(byte A, byte R, byte G, byte B)
{
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>解析 "#AARRGGBB"（契约外输入抛 <see cref="FormatException"/>）。</summary>
    public static Color32 Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 9 || value[0] != '#')
        {
            throw new FormatException($"Color32 的契约是 #AARRGGBB，实际：\"{value}\"");
        }
        return new Color32(
            Convert.ToByte(value[1..3], 16),
            Convert.ToByte(value[3..5], 16),
            Convert.ToByte(value[5..7], 16),
            Convert.ToByte(value[7..9], 16));
    }
}

/// <summary>
/// 行内样式（Phase 1 设计 §3.2）：可空字段为 null 时继承段落样式。
/// 首版显式不含：超链接、行内代码底色、字体族切换——均为「加字段即可向后兼容演进」。
/// </summary>
public sealed record InlineStyle(
    [property: JsonPropertyName("b")] bool Bold = false,
    [property: JsonPropertyName("i")] bool Italic = false,
    [property: JsonPropertyName("st")] bool Strikethrough = false,
    [property: JsonPropertyName("u")] bool Underline = false,
    [property: JsonPropertyName("c"), JsonConverter(typeof(Color32JsonConverter))] Color32? Color = null,
    [property: JsonPropertyName("r")] float? FontSizeRatio = null);
