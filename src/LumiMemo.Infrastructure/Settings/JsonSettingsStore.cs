using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Settings;

/// <summary>
/// <see cref="ISettingsStore"/> 的实现：<c>%LOCALAPPDATA%\LumiMemo\settings.json</c>（§8.2、§8.4）。
/// </summary>
/// <remarks>
/// <para>
/// 这个文件是<strong>系统边界</strong>：用户会手改它（§8.4 特意要求 <c>WriteIndented</c>
/// 就是为了这个）、同步工具会传它、旧版本会留下字段不全的版本。因此这里的每一处容错
/// 都对应一条明确的规则，而不是「顺手加个 try」。
/// </para>
/// <para>
/// <strong>读不出来 ≠ 损坏</strong>。文件被占用时退回默认值，但绝不改名挪走——
/// 那会把一份完好的配置变成「用户设置莫名其妙丢了」。只有确认内容读不懂时才备份。
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>本文件的当前版本（§8.4）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>自动保存去抖时长的合法下界（§9.3）。</summary>
    public const int MinAutoSaveDelayMs = 300;

    /// <summary>自动保存去抖时长的合法上界（§9.3）。</summary>
    public const int MaxAutoSaveDelayMs = 800;

    /// <summary>搜索去抖时长的合法下界（§9.3）。</summary>
    public const int MinSearchDebounceMs = 100;

    /// <summary>搜索去抖时长的合法上界（§9.3）。</summary>
    public const int MaxSearchDebounceMs = 500;

    /// <summary>附件目录名的兜底值，也是 <see cref="AppSettings.AttachmentsFolderName"/> 的默认值。</summary>
    private const string DefaultAttachmentsFolderName = "attachments";

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly ILogger<JsonSettingsStore> _logger;

    public JsonSettingsStore(
        IAppPaths paths,
        IClock clock,
        AtomicFileWriter writer,
        ILogger<JsonSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _clock = clock;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>
    /// 最近一次 <see cref="LoadAsync"/> 遇到的问题；一切正常时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 文件首次运行时不存在的<strong>不</strong>算问题，因此这种情况下它保持
    /// <see langword="null"/>——那是正常的首发状态，不是需要告诉用户的事。
    /// </remarks>
    public SettingsLoadProblem? LastProblem { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// 从未出过问题的文件里读出来的对象<strong>仍然会过一遍钳制</strong>：范围越界
    /// （比如手改成 <c>autoSaveDelayMs: 50</c>）只需日志记一笔，不用把整份配置作废。
    /// </remarks>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        LastProblem = null;

        string path = _paths.SettingsFile;
        string? json = await TryReadAllTextAsync(path, ct).ConfigureAwait(false);

        if (json is null)
        {
            return new AppSettings { Version = CurrentVersion };
        }

        if (JsonFileFormat.ReadVersion(json) > CurrentVersion)
        {
            // §8.4：来自更新版本的配置，不要尝试解析。解析到一半才失败会退化成
            // 「文件损坏」这条错误路径，把「换个版本的程序」这个真正的出路盖掉。
            string? backup = JsonFileFormat.TryArchiveCorrupt(path, _clock, _logger);
            LastProblem = new SettingsLoadProblem(
                SettingsLoadProblemKind.NewerVersion,
                backup,
                "配置文件由更新版本的程序创建，本次以默认设置启动。请改用较新版本的程序。");

            return new AppSettings { Version = CurrentVersion };
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileFormat.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "settings.json 无法解析（{ExceptionType}），本次以默认设置启动。",
                ex.GetType().Name);

            string? backup = JsonFileFormat.TryArchiveCorrupt(path, _clock, _logger);
            LastProblem = new SettingsLoadProblem(
                SettingsLoadProblemKind.Corrupt,
                backup,
                backup is null
                    ? "配置文件已损坏，本次以默认设置启动（原文件未能备份）。"
                    : $"配置文件已损坏，本次以默认设置启动。原文件已备份为 {backup}。");

            return new AppSettings { Version = CurrentVersion };
        }

        if (settings is null)
        {
            // 文件内容就是字面量 null。按损坏处理，与 §8.4「不要抛异常，用默认值」一致。
            return new AppSettings { Version = CurrentVersion };
        }

        Normalize(settings, path);

        return settings;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 同样整体走一遍钳制：调用方（设置窗口）理论上只会给出合法值，
    /// 但写盘前把值收进合法区间，能保证「磁盘上永远是一份合法的配置」，
    /// 不必依赖每个调用点都不出错。
    /// </para>
    /// <para>
    /// 钳制是<strong>就地</strong>改 <paramref name="settings"/> 的：调用方拿着的那个对象
    /// 与磁盘上的内容因此在任何时候都一致。否则设置窗口会显示 50 毫秒，
    /// 而实际生效的是 300 毫秒，用户只会觉得「改了没用」。
    /// </para>
    /// </remarks>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Normalize(settings, _paths.SettingsFile);

        byte[] bytes = JsonFileFormat.WithTrailingNewline(
            JsonSerializer.SerializeToUtf8Bytes(settings, JsonFileFormat.Options));

        await _writer.WriteAsync(_paths.SettingsFile, bytes, _clock.Now, ct).ConfigureAwait(false);
    }

    /// <summary>把设置里越界或非法的值收进合法范围。</summary>
    /// <param name="settings">就地修改。</param>
    /// <param name="path">只为日志能指认是哪个文件。</param>
    /// <remarks>
    /// 每个字段的规则都是 §9.3 明写的。改动这里之前先确认文档有没有跟着变，
    /// 这些范围同时被设置窗口的输入校验依赖。
    /// </remarks>
    private void Normalize(AppSettings settings, string path)
    {
        settings.Version = CurrentVersion;

        settings.AutoSaveDelayMs = Clamp(
            settings.AutoSaveDelayMs,
            MinAutoSaveDelayMs,
            MaxAutoSaveDelayMs,
            nameof(AppSettings.AutoSaveDelayMs),
            path);

        settings.SearchDebounceMs = Clamp(
            settings.SearchDebounceMs,
            MinSearchDebounceMs,
            MaxSearchDebounceMs,
            nameof(AppSettings.SearchDebounceMs),
            path);

        settings.AttachmentsFolderName = SanitizeAttachmentsFolderName(
            settings.AttachmentsFolderName,
            path);
    }

    private int Clamp(int value, int minimum, int maximum, string fieldName, string path)
    {
        if (value >= minimum && value <= maximum)
        {
            return value;
        }

        int clamped = Math.Clamp(value, minimum, maximum);

        // §9.3：超范围的值不生效，但也不报错，只记日志。
        _logger.LogWarning(
            "{Path} 的 {Field} 为 {Value}，超出 [{Minimum}, {Maximum}]，已按 {Clamped} 生效。",
            path,
            fieldName,
            value,
            minimum,
            maximum,
            clamped);

        return clamped;
    }

    /// <summary>
    /// 把附件目录名收成一个安全的<strong>相对目录名</strong>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个值会被拼成 <c>{笔记目录}\{附件目录名}</c>（§6.1、§8.1）。它来自一个用户可改、
    /// 也可能被同步过来的文件，属于系统边界，所以要挡一道：
    /// <c>"..\..\Windows"</c> 这种值会让附件目录跑到笔记目录外面去。
    /// </para>
    /// <para>
    /// 非法值<strong>退回默认的 <c>attachments</c> 而不是抛异常</strong>：一个名字写错
    /// 不该让程序起不来，退回默认值至少让附件功能照常可用。
    /// </para>
    /// </remarks>
    private string SanitizeAttachmentsFolderName(string? folderName, string path)
    {
        // 冒号一并挡掉：它既可能是驱动器号（C:），也可能是 NTFS 数据流（name:stream），
        // 两种都不该出现在一个「子目录名」里。
        if (string.IsNullOrWhiteSpace(folderName)
            || folderName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0
            || folderName is "." or "..")
        {
            _logger.LogWarning(
                "{Path} 的 AttachmentsFolderName 不是合法的目录名，已改用 {Fallback}。",
                path,
                DefaultAttachmentsFolderName);

            return DefaultAttachmentsFolderName;
        }

        return folderName;
    }

    /// <summary>读取整份文件；不存在、读不出来都返回 <see langword="null"/> 并已记日志。</summary>
    private async Task<string?> TryReadAllTextAsync(string path, CancellationToken ct)
    {
        string ensured = LongPath.Ensure(path);

        if (!File.Exists(ensured))
        {
            return null;
        }

        try
        {
            return await StorageRetry
                .RunAsync(() => File.ReadAllText(ensured), StorageRetry.DefaultDelaysMilliseconds, ct)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "读取 settings.json 失败，本次以默认设置启动：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);

            LastProblem = new SettingsLoadProblem(
                SettingsLoadProblemKind.Unreadable,
                BackupPath: null,
                "配置文件暂时读不出来（可能被其他程序占用），本次以默认设置启动。原文件未改动。");

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "读取 settings.json 失败，没有访问权限：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);

            LastProblem = new SettingsLoadProblem(
                SettingsLoadProblemKind.Unreadable,
                BackupPath: null,
                "配置文件没有访问权限，本次以默认设置启动。原文件未改动。");

            return null;
        }
    }
}
