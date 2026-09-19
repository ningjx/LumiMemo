using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Settings;

/// <summary>
/// <see cref="ILayoutStore"/> 的实现：整份 <c>layout.json</c> 的内存副本 + 原子落盘（§8.3、§8.5）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>本类完全不碰坐标换算</strong>。它只搬运物理像素，不认识 DPI 缩放、
/// 不知道窗口该摆哪（§8.3 要求坐标规则只有一处实现，那一处在
/// <c>LumiMemo.Core.Math.LayoutMath</c>）。唯一的例外是 <c>displays</c> 表的写入——
/// 那是「当时显示器长什么样」的快照，是记录而不是决策。
/// </para>
/// <para>
/// 保存策略是<strong>整体保存 + 触发点 + 节流</strong>（§8.5）：<see cref="MarkDirty"/>
/// 只置一个标志，真正的写盘发生在节流后的 <see cref="FlushAsync"/>。谁负责节流
/// 由上层决定（<c>LayoutService</c> 持有一个 1 秒的定时器）——本类不做定时，
/// 免得「什么时候写」这件事实现在两个地方。
/// </para>
/// </remarks>
public sealed class JsonLayoutStore : ILayoutStore
{
    /// <summary>本文件的当前版本（§8.4）。</summary>
    public const int CurrentVersion = 1;

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly AtomicFileWriter _writer;
    private readonly IDisplayProvider _displays;
    private readonly ILogger<JsonLayoutStore> _logger;

    private readonly Dictionary<Guid, NoteLayout> _layouts = [];

    private bool _dirty;
    private bool _loaded;

    public JsonLayoutStore(
        IAppPaths paths,
        IClock clock,
        AtomicFileWriter writer,
        IDisplayProvider displays,
        ILogger<JsonLayoutStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _clock = clock;
        _writer = writer;
        _displays = displays;
        _logger = logger;
    }

    // ---- 新建便签的默认尺寸 ----

    /// <summary>新建便签的默认宽度（物理像素）。由启动序列从设置里灌进来（§17.1）。</summary>
    /// <remarks>
    /// 与 <c>MarkdownNoteRepository.DefaultColor</c> 同一手法：做成可写属性而不是构造参数。
    /// 它来自设置，而设置可能在程序运行期间被改；做成构造参数的话，
    /// 用户改完默认尺寸就得重建整个存储。
    /// </remarks>
    public double DefaultWidth { get; set; } = 360;

    /// <inheritdoc cref="DefaultWidth" />
    public double DefaultHeight { get; set; } = 420;

    /// <inheritdoc />
    /// <remarks>
    /// 文件不存在是<strong>正常的首发状态</strong>，不是错误：什么都不做，
    /// 于是 <see cref="FlushAsync"/> 也不会平白造出一个内容全空的 <c>layout.json</c>。
    /// 用户第一次真正挪动窗口时才创建它。
    /// </remarks>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _layouts.Clear();
        _dirty = false;
        _loaded = true;

        string path = _paths.LayoutFile;
        string? json = await TryReadAllTextAsync(path, ct).ConfigureAwait(false);

        if (json is null)
        {
            return;
        }

        if (JsonFileFormat.ReadVersion(json) > CurrentVersion)
        {
            // §8.4：来自更新版本的配置不解析，改名保留后用默认值启动。
            JsonFileFormat.TryArchiveCorrupt(path, _clock, _logger);
            return;
        }

        LayoutFileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<LayoutFileModel>(json, JsonFileFormat.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("layout.json 无法解析（{ExceptionType}），本次以默认布局启动。", ex.GetType().Name);
            JsonFileFormat.TryArchiveCorrupt(path, _clock, _logger);
            return;
        }

        if (model?.Notes is null)
        {
            return;
        }

        int skipped = 0;
        foreach ((string key, NoteEntry entry) in model.Notes)
        {
            if (!Guid.TryParse(key, out Guid noteId))
            {
                // 一条坏键不该带走整份布局。丢掉它，其余照常恢复。
                skipped++;
                continue;
            }

            _layouts[noteId] = entry.ToLayout(noteId);
        }

        _logger.LogInformation(
            "载入 layout.json：{Count} 条布局，跳过 {Skipped} 条非法条目。",
            _layouts.Count,
            skipped);
    }

    /// <inheritdoc />
    public NoteLayout GetOrCreate(Guid noteId)
    {
        EnsureLoaded();

        if (_layouts.TryGetValue(noteId, out NoteLayout? existing))
        {
            return existing;
        }

        // 位置留 (0,0) 由调用方接管：新便签该摆哪要按 §13.8 的算位算法与层叠规则定，
        // 那个决策属于 LayoutService，不属于存储。
        var created = new NoteLayout
        {
            NoteId = noteId,
            Width = DefaultWidth,
            Height = DefaultHeight,
            ExpandedHeight = DefaultHeight,
        };

        _layouts[noteId] = created;

        // 新条目必须落盘：isOpen 默认为 true（§8.3 的文件级规则），
        // 不写下去的话下次启动就不知道这张便签上次是开着的。
        MarkDirty();

        return created;
    }

    /// <inheritdoc />
    public NoteLayout? TryGet(Guid noteId)
    {
        EnsureLoaded();

        return _layouts.GetValueOrDefault(noteId);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NoteLayout> All
    {
        get
        {
            EnsureLoaded();

            return _layouts.Values;
        }
    }

    /// <inheritdoc />
    public void MarkDirty() => _dirty = true;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 没有改动时<strong>一个字节都不写</strong>：否则「启动 → 退出」这样一个什么都没做的会话
    /// 也会重写 <c>layout.json</c>，改掉它的修改时间——那会干扰用户自己的备份与同步工具，
    /// 也让「文件什么时候变的」这条排查线索失真。
    /// </para>
    /// <para>
    /// 写失败只记 Warning（§8.5）。布局是设备状态，丢了顶多是窗口位置回到默认，
    /// 不影响用户数据，绝不为此打断用户操作。失败时<strong>保留脏标志</strong>，
    /// 下一次节流落盘还会再试一遍。
    /// </para>
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!_loaded || !_dirty)
        {
            return;
        }

        byte[] bytes = JsonFileFormat.WithTrailingNewline(
            JsonSerializer.SerializeToUtf8Bytes(BuildModel(), JsonFileFormat.Options));

        try
        {
            await _writer.WriteAsync(_paths.LayoutFile, bytes, _clock.Now, ct).ConfigureAwait(false);
            _dirty = false;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                "写入 layout.json 失败，窗口位置本次不会保留（{ExceptionType}）。",
                ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "写入 layout.json 失败，窗口位置本次不会保留（{ExceptionType}）。",
                ex.GetType().Name);
        }
    }

    /// <summary>把内存里的布局与当前显示器形态组装成待写入的模型。</summary>
    private LayoutFileModel BuildModel()
    {
        var model = new LayoutFileModel
        {
            Version = CurrentVersion,
            Displays = [],
            Notes = [],
        };

        foreach ((Guid noteId, NoteLayout layout) in _layouts)
        {
            // "D" 格式就是 §8.3 示例里的 3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33。
            // 读取方向用 Guid.TryParse，任何格式都认，所以这里的写法不必是契约。
            model.Notes[noteId.ToString("D")] = NoteEntry.From(layout);
        }

        // displays 表每次落盘都整体刷新：它记的是「此刻显示器长什么样」，
        // 沿用上一次的快照没有意义。
        foreach (DisplaySnapshot display in _displays.All)
        {
            model.Displays[display.DeviceId] = DisplayEntry.From(display);
        }

        return model;
    }

    /// <summary>读取整份文件；不存在、读不出来都返回 <see langword="null"/> 并已记日志。</summary>
    /// <remarks>
    /// <para>
    /// 读失败时<strong>绝不</strong>把文件当成「损坏」挪走。文件被同步工具或杀毒软件
    /// 独占几百毫秒是常事，把这个当成损坏会平白丢一份完好的布局；
    /// 重试一轮仍读不出来，就当这一轮没有布局，原文件原地不动。
    /// </para>
    /// <para>
    /// <c>File.ReadAllText</c> 会自动识别并吃掉 BOM，因此手改过、被记事本加上 BOM 的文件
    /// 也能正常解析。
    /// </para>
    /// </remarks>
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
                "读取 layout.json 失败，本次以默认布局启动：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "读取 layout.json 失败，本次以默认布局启动：{Path}（{ExceptionType}）。",
                path,
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>读盘之前就问「有哪些布局」，说明有人漏了启动序列里的一步，当场炸掉比返回空集合好。</summary>
    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException(
                "JsonLayoutStore.LoadAsync 必须先于任何读取调用（启动序列见 §17.1 第 7 步）。");
        }
    }
}
