using System.Text.Json.Serialization;

namespace LumiMemo.Core.Models;

/// <summary>自动标题使用的 OpenAI 兼容聊天接口配置。</summary>
public sealed class LlmSettings
{
    public const string DefaultPrompt = """
        为便笺拟一个中文标题。

        先判断这张便笺讲的是什么、属于哪个领域、派什么用（工作、会议、待办、采购、学习、技术、资料、想法、生活、健康、财务、出行……），再顺着这个领域去措辞。

        1. 用该领域内行的说法，让领域感从词里透出来——「评审纪要」「采购清单」「复现步骤」「取舍分析」，而不是「关于……的一些内容」。
        2. 不要把类别名当标签贴上去——不要「购物：」「【会议】」这类前缀；领域感靠措辞透出来就够了。
        3. 标题是名词短语，不是句子；并尽量带上这张便笺独有的信息：人名、项目、数字、结论。
        4. 只输出标题本身，不要引号、标点、序号、解释。最多 20 个汉字，宁短勿长。
        5. 内容太少、没有明确主题时照实概括，不要硬套领域。

        例：
        「周三去超市买牛奶鸡蛋，顺路退快递」→ 周三采购清单
        「P99 从 800ms 降到 90ms，根因是连接池没设上限」→ 连接池上限调优
        「和张工聊到离线优先，本地冲突合并比想的难」→ 离线优先的取舍
        """;

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
