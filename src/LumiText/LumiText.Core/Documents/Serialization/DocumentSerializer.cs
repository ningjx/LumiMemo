using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiText.Core.Documents.Serialization;

/// <summary>
/// .lumi v2 正文 JSON 的根信封：<c>{"schema":1,"blocks":[...],"images":[...]}</c>（§3.3 契约）。
/// <c>schema</c> 是正文结构自身的版本号，与 .lumi 文件级 Version 解耦。
/// </summary>
internal sealed record DocumentEnvelope(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("blocks")] IReadOnlyList<Block>? Blocks,
    [property: JsonPropertyName("images")] IReadOnlyList<ImageResource>? Images = null);

/// <summary>
/// <see cref="Document"/> ↔ .lumi v2 正文 JSON 的序列化器（只冻结契约，不接存储层——
/// 存储落地是 Phase 2 的事，Phase 1 设计 §0「不做」清单）。
/// </summary>
/// <remarks>
/// 容错契约（沿用 LumiNoteStorage 的容错矩阵语义）：
/// schema 高于当前版本 → 返回 <see langword="null"/>（高版本跳过）；
/// 未知字段 → 忽略（向后兼容演进）；JSON 结构损坏 → 抛 <see cref="JsonException"/>，
/// 由调用方按「坏文件跳过并记日志」处理。
/// </remarks>
public static class DocumentSerializer
{
    /// <summary>序列化为 v2 正文 JSON（images 为空表时省略该字段）。</summary>
    public static string Serialize(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var envelope = new DocumentEnvelope(
            Document.SchemaVersion,
            document.Blocks,
            document.Images is { Count: > 0 } images ? images : null);
        return JsonSerializer.Serialize(envelope, LumiTextJsonContext.Instance.DocumentEnvelope);
    }

    /// <summary>解析 v2 正文 JSON。</summary>
    /// <returns>
    /// 解析成功的文档；schema 高于 <see cref="Document.SchemaVersion"/> 时返回
    /// <see langword="null"/>（高版本跳过）。schema 缺省视为当前版本（宽容读取）。
    /// </returns>
    public static Document? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var envelope = JsonSerializer.Deserialize(json, LumiTextJsonContext.Instance.DocumentEnvelope);
        if (envelope is null || envelope.Schema > Document.SchemaVersion)
        {
            return null;
        }
        return new Document(envelope.Blocks ?? [], envelope.Images);
    }
}
