using System.Text.Json.Serialization;

namespace LumiMemo.Core.Models;

/// <summary>自动标题使用的 OpenAI 兼容聊天接口配置。</summary>
public sealed class LlmSettings
{
    public const string DefaultPrompt = """
        为便笺拟一个中文标题。

        先判断两件事：这张便笺是什么（一份清单？一份纪要？一段笔记？一条待办？一篇随笔？——由内容自己定，不限于这几种），以及它讲的是什么。标题就是这两个答案的组合。

        1. 标题写成「是什么 + 讲什么」：「周三采购清单」「下季度排期评审纪要」「连接池上限排查」「平凡生活的随想」。前一半是标题的一部分，不是贴在前面的标签——不要写「购物：牛奶鸡蛋」，直接写「周三采购清单」。
        2. 前一半要名副其实：由内容自己给出，套不上的时候别硬套（随手记的碎片不必硬说成"笔记"）。
        3. 后一半尽量带上这张便笺独有的信息：人名、项目、数字、结论；没有具体信息时把主旨直说。
        4. 不要用修辞代替信息：比喻、意象、抒情化的写法都不是便笺标题该有的东西。
        5. 只输出标题本身，不要引号、标点、序号、解释。最多 20 个汉字，宁短勿长。

        例：
        「周三去超市买牛奶鸡蛋，顺路退快递」→ 周三采购清单
        「P99 从 800ms 降到 90ms，根因是连接池没设上限」→ 连接池上限排查
        「和张工聊到离线优先，本地冲突合并比想的难」→ 离线优先的取舍
        「生活有时候像一条安静的河流……（一篇讲平凡日子的散文）」→ 平凡生活的随想
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
