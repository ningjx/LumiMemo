using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>为便笺正文生成短标题的外部服务边界。</summary>
public interface ITitleGenerator
{
    Task<string> GenerateAsync(string content, LlmSettings settings, CancellationToken ct = default);
}
