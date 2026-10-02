using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiText.Core.Documents.Serialization;

/// <summary><see cref="Color32"/> ↔ "#AARRGGBB" 字符串的 JSON 转换。</summary>
public sealed class Color32JsonConverter : JsonConverter<Color32>
{
    public override Color32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return Color32.Parse(reader.GetString()
                ?? throw new JsonException("Color32 需要 #AARRGGBB 字符串，实际为 null。"));
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, Color32 value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
