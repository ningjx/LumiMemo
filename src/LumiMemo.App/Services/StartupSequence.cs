using System.IO;
using LumiMemo.App.Abstractions;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// §17.1 的启动序列（第 2–7 步）与 §17.4 的退出收尾。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它是设置与各存储之间唯一的串联者。</strong> <see cref="IAppPaths"/> 是只读的，
/// <c>SetNotesFolder</c> 只在具体类上有，而 §4.3 规定组合根是唯一知道具体类型的地方——
/// 于是「把设置灌进对象图」这件事必须由一个明确的服务来做，不能散在 <c>App</c> 里，
/// 否则它会长成一个什么都干的 God class。
/// </para>
/// <para>
/// <strong>它注入具体存储类型而不是接口</strong>（<c>JsonLayoutStore</c>、
/// <c>MarkdownNoteRepository</c>）：默认宽度、默认颜色这些「来自设置、运行期可改」的值
/// 恰恰只在具体类型上（接口不该为它们开口子）。这正是 §4.3 那条规则的意思——
/// 知道具体类型的代码要收敛，但总得有地方知道。
/// </para>
/// <para>
/// <strong>本轮与文档的偏离</strong>：§17.1 第 6 步是「按 layout 恢复上次打开的便签窗口」，
/// 本轮<strong>不做</strong>——用户已裁决首次打开程序不自动弹便签，只开管理器。
/// 恢复逻辑等托盘与「显示全部」接上后一并补，那时它才有对应的手工入口可以验证。
/// </para>
/// </remarks>
public sealed class StartupSequence
{
    private readonly AppPaths _paths;
    private readonly ISettingsStore _settingsStore;
    private readonly JsonLayoutStore _layoutStore;
    private readonly LayoutService _layoutService;
    private readonly MarkdownNoteRepository _repository;
    private readonly INoteService _noteService;
    private readonly AutoSaveService _autoSaveService;
    private readonly WindowManager _windowManager;
    private readonly IFolderPicker _folderPicker;
    private readonly ManagerViewModel _manager;
    private readonly ManagerWindow _managerWindow;
    private readonly ILogger<StartupSequence> _logger;

    public StartupSequence(
        AppPaths paths,
        ISettingsStore settingsStore,
        JsonLayoutStore layoutStore,
        LayoutService layoutService,
        MarkdownNoteRepository repository,
        INoteService noteService,
        AutoSaveService autoSaveService,
        WindowManager windowManager,
        IFolderPicker folderPicker,
        ManagerViewModel manager,
        ManagerWindow managerWindow,
        ILogger<StartupSequence> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(layoutService);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(autoSaveService);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(managerWindow);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _settingsStore = settingsStore;
        _layoutStore = layoutStore;
        _layoutService = layoutService;
        _repository = repository;
        _noteService = noteService;
        _autoSaveService = autoSaveService;
        _windowManager = windowManager;
        _folderPicker = folderPicker;
        _manager = manager;
        _managerWindow = managerWindow;
        _logger = logger;
    }

    /// <summary>
    /// 跑完整个启动序列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>全程不 <c>ConfigureAwait(false)</c>，这是刻意的。</strong>
    /// 调用方在 UI 线程上发起它，于是每个 <c>await</c> 之后的续体都回到 UI 线程——
    /// 而 <c>NoteService.LoadAllAsync</c> 会写 <c>NoteStore</c> 与 <c>SearchIndex</c>，
    /// <c>_manager.Refresh()</c> 会改被界面绑定的 <c>ObservableCollection</c>，
    /// 两者都<strong>必须在 UI 线程上做</strong>（§3.4 规则 T1、T5）。
    /// 在这里加一个 <c>ConfigureAwait(false)</c>，整套线程约定就塌了，
    /// 而且症状（随机的跨线程异常）离原因很远。
    /// </para>
    /// <para>
    /// 本方法<strong>不吞异常</strong>：启动失败应当让用户看见并退出进程，
    /// 而不是留下一个只有半个对象图的僵尸程序。由调用方负责提示与退出。
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // §17.1 第 2 步：载入 settings.json。任何异常都已经在存储层降级成默认值了。
        AppSettings settings = await _settingsStore.LoadAsync(ct);

        // §17.1 第 3 步：确定笔记目录，没有就当面问用户。
        await EnsureNotesFolderAsync(settings, ct);

        // 把设置灌进各个消费方。必须在任何一次 LoadAsync / SaveAsync 之前做完，
        // 否则第一次扫描出来的便签会用默认尺寸、默认颜色。
        ApplySettings(settings);

        // §17.1 第 4 步：载入 layout.json（不存在时是正常的首发状态，什么都不做）。
        await _layoutService.LoadAsync(ct);

        // §17.1 第 5 步：扫描笔记目录，建 NoteStore 与索引。
        await _noteService.LoadAllAsync(ct);

        _manager.Refresh();

        // 管理器是程序的落脚点：关掉它才退出进程（本轮的临时语义，托盘接上后改为收进托盘）。
        _managerWindow.Show();
        _managerWindow.Activate();
    }

    /// <summary>
    /// §17.4 的退出收尾，按顺序做该做的几件事。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>顺序不能改</strong>：先停自动保存（否则它会在关窗途中又排一次写盘），
    /// 再 flush 未落盘的便签内容，最后才写 layout.json——
    /// 反过来的话，flush 过程中窗口位置的变化就写不进 layout 了。
    /// </para>
    /// <para>
    /// <strong>整体扔到线程池上跑</strong>：这些 <c>async</c> 方法的续体默认要回
    /// UI 线程，而调用方（<c>App.OnExit</c>）正<strong>阻塞</strong>着 UI 线程等它完成，
    /// 于是必然死锁。<c>Task.Run</c> 让整个 flush 彻底脱离 UI 线程，
    /// 每个 <c>ConfigureAwait(false)</c> 于是都真的换个线程继续，谁也等不到谁。
    /// </para>
    /// <para>
    /// 名字带 <c>...AndWait</c> 是因为它真的会<strong>阻塞调用线程</strong>——
    /// 这在 WPF 里通常是禁忌，唯一能这么写的地方就是 <c>OnExit</c>：
    /// 那时消息泵已经要停了，除了等没有别的选择。
    /// </para>
    /// </remarks>
    public void ShutdownAndWait()
    {
        Task.Run(async () =>
        {
            _autoSaveService.Dispose();

            await _noteService.SaveAllAsync().ConfigureAwait(false);
            await _layoutService.FlushNowAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 确定笔记目录：能用就用，不能用就问用户，用户取消就落到默认目录。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「配过但目录已经不在了」（U 盘拔了、网盘离线）与「从没配过」走同一条路，
    /// 都去问用户。区别只在传给文件夹选择框的初始位置不同。
    /// </para>
    /// <para>
    /// <strong>用户取消时的退路文档里没定义</strong>（原设计的向导有「使用默认位置」按钮）。
    /// 这里退回 <c>我的文档\LumiMemo</c> 并创建，而不是退出程序——
    /// 直接退出会留下一个「双击了但什么都没发生」的观感，而用户其实只是点错了按钮。
    /// </para>
    /// </remarks>
    private async Task EnsureNotesFolderAsync(AppSettings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.NotesFolder)
            && Directory.Exists(settings.NotesFolder))
        {
            _paths.SetNotesFolder(settings.NotesFolder);

            return;
        }

        string? initialDirectory = Directory.Exists(settings.NotesFolder)
            ? settings.NotesFolder
            : null;

        if (!string.IsNullOrWhiteSpace(settings.NotesFolder))
        {
            _logger.LogInformation(
                "设置里记的笔记目录 {Folder} 已不存在，重新询问。",
                settings.NotesFolder);
        }

        string? picked = _folderPicker.PickFolder("选择存放便签的文件夹", initialDirectory);

        string folder = picked ?? DefaultNotesFolder();

        if (picked is null)
        {
            _logger.LogInformation("用户取消了目录选择，改用默认目录 {Folder}。", folder);
        }

        // 目录可能是刚刚手输进去的、也可能压根不存在的路径，建出来。
        Directory.CreateDirectory(folder);
        _paths.SetNotesFolder(folder);

        settings.NotesFolder = folder;

        // 立刻存一次：用户下次启动不该被再问一遍。
        await _settingsStore.SaveAsync(settings, ct);
    }

    private void ApplySettings(AppSettings settings)
    {
        _paths.SetAttachmentsFolderName(settings.AttachmentsFolderName);

        _layoutStore.DefaultWidth = settings.DefaultWidth;
        _layoutStore.DefaultHeight = settings.DefaultHeight;
        _layoutStore.DefaultContentScale = settings.DefaultContentScale;

        _repository.DefaultColor = settings.DefaultColor;

        _layoutService.ShowStatusBar = settings.ShowStatusBar;
        _autoSaveService.DelayMilliseconds = settings.AutoSaveDelayMs;
        _windowManager.RestoreAfterShowDesktop = settings.RestoreAfterShowDesktop;
        _manager.SearchDebounceMilliseconds = settings.SearchDebounceMs;
    }

    /// <summary><c>我的文档\LumiMemo</c>。用户取消选择目录时的退路。</summary>
    private static string DefaultNotesFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "LumiMemo");
}
