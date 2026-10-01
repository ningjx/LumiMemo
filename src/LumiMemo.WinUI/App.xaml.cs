using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Llm;
using LumiMemo.Infrastructure.Logging;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Trash;
using LumiMemo.Infrastructure.Windows;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;

namespace LumiMemo.WinUI;

/// <summary>WinUI 应用入口：单实例把关 + 两段式组合根 + 启动编排。</summary>
/// <remarks>
/// <para>
/// <strong>两段式</strong>：设置是异步读的，而依赖设置的实例（笔记目录、各 Store、便笺快照）
/// 只能在设置之后构造——所以先手工引导前段，再整体组装容器。容器本身
/// 与微软官方的 WinUI 3 架构模式一致（<c>ServiceCollection</c> + <c>GetService&lt;T&gt;</c>）。
/// </para>
/// <para>
/// per-note 的窗口/ViewModel 不进容器（构造参数来自便签本身），走
/// <see cref="NoteWindowFactory"/>；托盘留在这里 new，因为它的回调闭包会引用容器里的服务，
/// 进容器会绕成环。
/// </para>
/// </remarks>
public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayIconService? _trayIcon;
    private SingleInstanceGuard? _guard;
    private FileLoggerProvider? _fileLogger;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>容器取值入口；注册缺失在 <c>ValidateOnBuild</c> 时就会失败，这里兜底报错。</summary>
    public static T GetService<T>()
        where T : notnull
    {
        ServiceProvider? services = (Current as App)?._services;
        if (services is null)
        {
            throw new InvalidOperationException($"应用尚未初始化，取不到 {typeof(T)}。");
        }

        return services.GetRequiredService<T>();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // 单实例：先立把门人。第二个实例通知第一个之后自己退出——
            // 「便笺只允许本程序修改」这条约定的前提就是同一时刻只有一个本程序。
            _guard = new SingleInstanceGuard(
                SingleInstanceChannel.MutexName,
                SingleInstanceChannel.PipeName,
                NullLogger.Instance);
            if (!_guard.IsFirstInstance)
            {
                _ = SingleInstanceChannel.TrySignal(
                    SingleInstanceChannel.PipeName, TimeSpan.FromSeconds(2));
                Exit();
                return;
            }

            _services = await BuildServicesAsync();

            NoteWindowManager windows = GetService<NoteWindowManager>();
            ManagerWindow manager = GetService<ManagerWindow>();

            // 再开一个实例时打开便笺列表（管道线程 → UI 线程，§3.4 规则 T5）。
            // 不再求「唤出全部便笺」：便签一多，全铺到桌面上没有意义（2026-10 起只留列表入口）。
            _guard.SecondInstanceSignalled += (_, _) =>
                Microsoft.UI.Dispatching.DispatcherQueue
                    .GetForCurrentThread()
                    ?.TryEnqueue(manager.ShowWindow);
            _guard.StartListening();

            AppSettings settings = GetService<AppSettings>();

            // 回收站过期清理。失败只记日志：维护动作不该拦住启动或弹任何东西。
            try
            {
                TimeSpan retention = TimeSpan.FromDays(settings.TrashRetentionDays);
                await GetService<ITrashStore>().PurgeExpiredAsync(retention);
            }
            catch (Exception exception)
            {
                GetService<ILogger<App>>().LogWarning(
                    "回收站过期清理失败（{ExceptionType}）。", exception.GetType().Name);
            }

            if (settings.ShowTrayIcon)
            {
                _trayIcon = new TrayIconService(manager.TrayIconHost);
                _trayIcon.Start(
                    () => _ = windows.CreateNoteAsync(),
                    windows.HideAllNotes,
                    manager.ShowWindow,
                    () => OpenNotesFolder(settings.NotesFolder),
                    () => _ = manager.ShowAboutAsync(),
                    ExitFromTray);
            }

            int restored = windows.RestoreOpenNotes();
            if (restored == 0)
            {
                manager.ShowWindow();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            Exit();
        }
    }

    private static void OpenNotesFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private async void ExitFromTray()
    {
        ILogger<App> logger = GetService<ILogger<App>>();

        // 每一步都立即冲刷日志：崩溃时最后一条落盘的日志就是精确的现场。
        void Step(string message)
        {
            logger.LogInformation(message);
            _fileLogger?.Flush(TimeSpan.FromSeconds(1));
        }

        Step("退出：开始（逐窗保存并关闭）。");

        try
        {
            // 逐窗保存并关闭；保存失败只留日志（「退出总会发生」）。
            await GetService<NoteWindowManager>().CloseAllForExitAsync();
            Step("退出：便签窗口已全部关闭。");

            // 设备状态最后统一落盘。
            await GetService<ILayoutStore>().FlushAsync();
            Step("退出：layout 已落盘。");

            GetService<ManagerWindow>().CloseForExit();
            Step("退出：管理器已关闭。");
        }
        catch (Exception exception)
        {
            Step($"退出：关闭阶段出错（{exception.GetType().Name}），继续收尾。");
        }

        // 托盘控件在窗口关干净之后释放：它挂在管理器窗口的树里。
        _trayIcon?.Dispose();
        _trayIcon = null;
        Step("退出：托盘已释放。");

        // 把门人最后放：收尾期间不允许另一个实例抢跑进来。
        _guard?.Dispose();
        _guard = null;
        Step("退出：单实例守卫已释放。");

        Step("退出：准备释放容器。");
        _services?.Dispose();
        _services = null;

        // 到这里便笺、layout、日志都已落盘，容器也已释放——直接结束进程。
        // 不用 Application.Exit()：它会走 WinUI 的整套退出清理面，那里正是
        // 这个应用反复以 exit 139 猝死的地方（2026-10-01 的排查）。
        Environment.Exit(0);
    }

    /// <summary>第一段（异步引导）+ 第二段（容器组装）。</summary>
    private async Task<ServiceProvider> BuildServicesAsync()
    {
        var paths = new AppPaths();
        paths.EnsureLocalAppDataDirectories();

        var clock = new SystemClock();
        var writer = new AtomicFileWriter(clock);

        // 日志先按默认级别立起来；设置读出来后再把级别调过去（provider 支持运行期改）。
        _fileLogger = new FileLoggerProvider(paths.LogDirectory, clock);
        using var bootstrapFactory = LoggerFactory.Create(
            builder => builder.AddProvider(_fileLogger));

        var settingsStore = new JsonSettingsStore(
            paths, clock, writer, bootstrapFactory.CreateLogger<JsonSettingsStore>());
        AppSettings settings = await settingsStore.LoadAsync();

        if (!FileLoggerProvider.TryParseMinimumLevel(settings.LogLevel, out LogLevel level))
        {
            bootstrapFactory.CreateLogger<App>().LogWarning(
                "settings.json 里的 logLevel 认不出来，已退回 Information。");
        }

        _fileLogger.MinimumLevel = level;

        if (string.IsNullOrWhiteSpace(settings.NotesFolder))
        {
            settings.NotesFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "LumiMemo");
            Directory.CreateDirectory(settings.NotesFolder);
            await settingsStore.SaveAsync(settings);
        }

        paths.SetNotesFolder(settings.NotesFolder);
        paths.SetAttachmentsFolderName(settings.AttachmentsFolderName);

        var storage = new LumiNoteStorage(
            settings.NotesFolder,
            clock,
            writer,
            settings.DefaultColor,
            bootstrapFactory.CreateLogger<LumiNoteStorage>());

        IReadOnlyList<Note> notes = await storage.LoadAllAsync();

        var trash = new FileSystemTrashStore(
            settings.NotesFolder, clock, writer, bootstrapFactory.CreateLogger<FileSystemTrashStore>());

        var layoutStore = new JsonLayoutStore(
            paths,
            clock,
            writer,
            new MonitorEnumerator(),
            bootstrapFactory.CreateLogger<JsonLayoutStore>())
        {
            DefaultWidth = settings.DefaultWidth,
            DefaultHeight = settings.DefaultHeight,
        };
        await layoutStore.LoadAsync();

        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddProvider(_fileLogger);
            builder.SetMinimumLevel(_fileLogger.MinimumLevel);
        });

        services.AddSingleton(settings);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISettingsStore>(settingsStore);
        services.AddSingleton<ILayoutStore>(layoutStore);
        services.AddSingleton<INoteStorage>(storage);
        services.AddSingleton<ITrashStore>(trash);
        services.AddSingleton(notes);
        services.AddSingleton(httpClient);
        services.AddSingleton<ITitleGenerator>(new OpenAiCompatibleTitleGenerator(httpClient));
        services.AddSingleton(provider => new NoteTitleCoordinator(
            provider.GetRequiredService<IReadOnlyList<Note>>(),
            provider.GetRequiredService<INoteStorage>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<ITitleGenerator>()));
        services.AddSingleton<IUiTimerFactory>(new DispatcherQueueUiTimerFactory(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton(provider =>
        {
            var autoSave = new AutoSaveService(
                provider.GetRequiredService<IUiTimerFactory>(),
                provider.GetRequiredService<ILogger<AutoSaveService>>());
            autoSave.DelayMilliseconds = provider.GetRequiredService<AppSettings>().AutoSaveDelayMs;
            return autoSave;
        });
        services.AddSingleton<NoteWindowFactory>();
        services.AddSingleton<NoteWindowManager>();
        services.AddSingleton<ManagerViewModel>();
        services.AddSingleton<ManagerWindow>();
        services.AddSingleton<TrashViewModel>();
        services.AddSingleton<TrashWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
        });
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}
