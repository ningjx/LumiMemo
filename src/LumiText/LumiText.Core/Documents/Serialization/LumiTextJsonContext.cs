using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiText.Core.Documents.Serialization;

/// <summary>
/// .lumi v2 正文的源生成序列化上下文（AOT/裁剪友好，避免运行时反射）。
/// 字段名/可空语义/未知字段忽略策略由 T-S 系列往返单测钉死（Phase 1 设计 §10.1）。
/// </summary>
/// <remarks>
/// 不用 <see cref="JsonSourceGenerationOptionsAttribute"/> 而经构造函数传 options，
/// 是为了设置 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>（非 ASCII 原文写入，
/// 与宿主仓 JsonFileFormat 的既有约定对齐；attribute 无法表达 Encoder）。
/// </remarks>
[JsonSerializable(typeof(DocumentEnvelope))]
internal sealed partial class LumiTextJsonContext : JsonSerializerContext
{
    /// <summary>库内唯一使用的上下文实例（options 已冻结，见上）。</summary>
    public static readonly LumiTextJsonContext Instance = new(CreateOptions());

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
