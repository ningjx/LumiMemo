using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 位图资源表条目（.lumi v2 图片存储）：base64 内嵌单文件（O3，沿用 ADR 0002
/// 「一张便笺一个文件 + 原子写入」；单图压缩后 ≤ 5MB 的约束由编辑期插入入口执行）。
/// </summary>
public sealed record ImageResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("data")] byte[] Data);
