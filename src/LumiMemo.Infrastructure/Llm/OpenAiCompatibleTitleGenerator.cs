using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Infrastructure.Llm;

/// <summary>调用 OpenAI 兼容的 chat completions 接口生成便笺标题（<see cref="ITitleGenerator"/> 的实现）。</summary>
/// <remarks>
/// 放在 Infrastructure 而不是 UI 项目：它是 HTTP 基础设施，与「哪个界面在用」无关。
/// </remarks>
public sealed class OpenAiCompatibleTitleGenerator(HttpClient httpClient) : ITitleGenerator
{
    public async Task<string> GenerateAsync(string content, LlmSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);

        string endpoint = settings.Endpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("API 端点必须是 HTTPS 地址；本机服务可以使用 HTTP。", nameof(settings));
        }

        if (!uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri(endpoint.TrimEnd('/') + "/chat/completions");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", settings.ApiKey);
        }

        request.Content = JsonContent.Create(new
        {
            model = settings.Model.Trim(),
            messages = new[]
            {
                new { role = "system", content = settings.Prompt },
                new { role = "user", content = content.Length > 4000 ? content[..4000] : content }
            },
            stream = false,

            // DeepSeek V4 系列的思考模式默认开启（thinking.type 默认 enabled），
            // 不显式关掉的话每次生成标题都会先跑一整段思维链——非流式下还要等
            // 「思考 + 标题」全部输出完才返回，这是标题变慢的主要来源。
            // 拟一个 20 字标题不需要思考，显式关闭。
            //
            // 注意：thinking 是 DeepSeek 的扩展字段；换用其他 OpenAI 兼容服务时
            // 若遇到「未知参数」类报错，先删掉这一行再试。
            thinking = new { type = "disabled" }
        });

        using HttpResponseMessage response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        string? raw = json.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        string title = raw?.Trim().Trim('"', '\'', '“', '”', '#', ' ', '\r', '\n') ?? "";
        title = title.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (title.Length == 0)
        {
            throw new InvalidDataException("模型没有返回标题。");
        }

        return string.Concat(title.EnumerateRunes().Take(40).Select(rune => rune.ToString()));
    }
}
