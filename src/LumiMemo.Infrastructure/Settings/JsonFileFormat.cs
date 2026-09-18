using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LumiMemo.Core.Abstractions;
using LumiMemo.Infrastructure.Io;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Settings;

/// <summary>
/// 两个配置文件（<c>settings.json</c>、<c>layout.json</c>）共用的读写规则（§8.4）。
/// </summary>
/// <remarks>
/// 两份配置的序列化选项与坏文件处理必须完全一致，否则会出现「设置能恢复、布局不能」
/// 这种莫名其妙的不对称。这里放的是两者共同的部分，版本号各自持有——
/// 两个文件的版本是可以独立演进的。
/// </remarks>
internal static class JsonFileFormat
{
    /// <summary>
    /// 反序列化与序列化共用的选项（§8.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 是<strong>必需</strong>的：
    /// 默认编码器会把中文路径（<c>D:\笔记</c>）转义成 <c>D:\u7B14\u8BB0</c>，
    /// 用户打开配置文件会以为文件坏了。
    /// </para>
    /// <para>
    /// 允许注释与尾随逗号、且不区分键名大小写，都是为「用户手改过这个文件」准备的。
    /// 这三条容错不会掩盖任何真实问题：它们只放宽语法层面，
    /// 认不出来的键照样被忽略、类型不符照样抛 <see cref="JsonException"/>。
    /// 反过来，若因为用户顺手写了一句注释就把整份配置当成损坏文件挪走，
    /// 那才是真正的伤害。
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// 读出文件顶层的 <c>version</c>（§8.4）。
    /// </summary>
    /// <param name="json">文件全文。</param>
    /// <returns>版本号；字段缺失、不是整数、或根本不是 JSON 对象时返回 1。</returns>
    /// <remarks>
    /// <strong>不用反序列化读版本号</strong>：这正是本方法存在的理由。将来版本 2 的配置里
    /// 可能有本版本不认识的字段类型，先反序列化再读版本会在读到版本号之前就抛异常，
    /// 于是「来自更新版本」这条正常的兼容路径被误判成「文件损坏」。
    /// 逐字段地看版本号，前面什么都不碰。
    /// </remarks>
    public static int ReadVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return CurrentVersionFallback;
        }

        if (root is not JsonObject obj || !obj.TryGetPropertyValue("version", out JsonNode? node))
        {
            return CurrentVersionFallback;
        }

        return node is JsonValue value && value.TryGetValue(out int version)
            ? version
            : CurrentVersionFallback;
    }

    /// <summary>
    /// 把读不懂的配置文件改名挪到一边，保留现场（§8.3、§8.4）。
    /// </summary>
    /// <param name="path">原文件路径。</param>
    /// <param name="clock">时间戳来源。</param>
    /// <param name="logger">日志。</param>
    /// <returns>备份文件的路径；改名失败时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 命名取 §8.3 表格里的 <c>layout.json.corrupt-{时间戳}</c>，<c>settings.json</c> 同理。
    /// </para>
    /// <para>
    /// <strong>不用覆盖式改名</strong>：用户可能连续几次启动都撞上同一个损坏文件，
    /// 每次都撞在同一秒里，<c>File.Move</c> 会抛「目标已存在」，
    /// 于是第一次备份之后再也备不成——而那时候现场正是最该留下来的。
    /// 撞名时加数字后缀，够用了。
    /// </para>
    /// <para>
    /// 改名失败只记 Warning，<strong>不抛异常</strong>：调用方此刻正要用默认值继续启动，
    /// 因为一个备份动作失败而让程序起不来，是拿次要的事挡住了主要的事。
    /// </para>
    /// </remarks>
    public static string? TryArchiveCorrupt(string path, IClock clock, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        string timestamp = clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string backup = $"{path}.corrupt-{timestamp}";

        for (int suffix = 1; File.Exists(LongPath.Ensure(backup)); suffix++)
        {
            backup = $"{path}.corrupt-{timestamp}-{suffix}";
        }

        try
        {
            File.Move(LongPath.Ensure(path), LongPath.Ensure(backup));
            logger.LogWarning("配置文件无法使用，已备份到 {Backup}，本次以默认值启动。", backup);
            return backup;
        }
        catch (IOException ex)
        {
            logger.LogWarning(
                "备份损坏的配置文件失败：{Path}（{ExceptionType}），本次仍以默认值启动。",
                path,
                ex.GetType().Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(
                "备份损坏的配置文件失败：{Path}（{ExceptionType}），本次仍以默认值启动。",
                path,
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>给序列化出来的 JSON 字节补一个结尾换行。</summary>
    /// <remarks>
    /// 没有结尾换行的文本文件在不少编辑器与 <c>git diff</c> 里会显示成「最后一行被吞了」，
    /// 而这个文件是明确要给人看的。
    /// </remarks>
    public static byte[] WithTrailingNewline(byte[] utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        var result = new byte[utf8Json.Length + 1];
        utf8Json.CopyTo(result, 0);
        result[^1] = (byte)'\n';

        return result;
    }

    /// <summary><c>version</c> 缺失或读不出来时使用的版本号（§8.4：「缺省视为 1」）。</summary>
    private const int CurrentVersionFallback = 1;
}
