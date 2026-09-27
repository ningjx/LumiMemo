using System.Text.Json.Serialization;

namespace LumiMemo.Core.Models;

/// <summary>自动标题使用的 OpenAI 兼容聊天接口配置。</summary>
public sealed class LlmSettings
{
    public const string DefaultPrompt = "请根据便笺内容拟一个简短、准确的中文标题。只输出标题，不要引号、标点或解释，最多 20 个汉字。";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = DefaultPrompt;

    /// <summary>API 密钥，以明文保存在本机 settings.json。</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
}
