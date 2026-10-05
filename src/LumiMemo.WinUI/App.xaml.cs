using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
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
public partial class App : Application, IAppLifecycle
{
    private ServiceProvider? _services;
    private TrayIconService? _trayIcon;
    private SingleInstanceGuard? _guard;
    private FileLoggerProvider? _fileLogger;

    /// <summary>收尾是否已经开始。所有调用都在 UI 线程上，不需要再加锁。</summary>
    private bool _shuttingDown;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
            if (_services is null)
            {
                // 用户在首次运行的存储位置向导里放弃了：没有笔记目录，启动无从继续。
                Exit();
                return;
            }

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
                // 「打开笔记文件夹」认本次启动生效的目录，不读设置对象里的实时值：
                // 用户在设置页改了存储位置、还没重启的那段窗口期里，便签其实还在旧目录。
                string activeNotesFolder = settings.NotesFolder;

                _trayIcon = new TrayIconService(manager.TrayIconHost);
                _trayIcon.Start(
                    () => _ = windows.CreateNoteAsync(),
                    windows.HideAllNotes,
                    manager.ShowWindow,
                    () => OpenNotesFolder(activeNotesFolder),
                    () => _ = manager.ShowAboutAsync(),
                    () => _ = ShutdownAsync(relaunch: false));
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

    /// <summary>重新启动本程序；成功返回 <see langword="true"/>。</summary>
    /// <remarks>
    /// <c>UseShellExecute</c> 走的是与用户双击图标同一条路——直接 CreateProcess 会沿用
    /// 本进程的启动上下文，而 WinUI 在那套上下文里解析 XAML 资源会失败。
    /// </remarks>
    private static bool RelaunchSelf()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception)
        {
            // 拉不起来就只是「没重启」：设置已经存好，用户手动再开一次即可。
            return false;
        }
    }

    /// <inheritdoc />
    public void Relaunch() => _ = ShutdownAsync(relaunch: true);

    /// <summary>
    /// 收尾并结束进程；<paramref name="relaunch"/> 为 <see langword="true"/> 时先拉起一个新进程，
    /// 让「改了存储位置」这类必须重启才生效的设置落到实处。
    /// </summary>
    /// <remarks>
    /// 顺序是刻意的：释放动作都发生在新进程起来<strong>之前</strong>——单实例守卫一释放，
    /// 新进程才会认为自己是第一个实例；而容器一释放日志 provider 就没了，
    /// 「已拉起新进程」这条记录必须写在它前面。
    /// </remarks>
    private async Task ShutdownAsync(bool relaunch)
    {
        // 防重入：收尾要跑好几个 await，连点两次「立即重启」会走两遍并拉起两个新进程。
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

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

            // 退出时不再关闭管理器窗口：它的原生 Close() 反复以 0xC000027B（存置异常，
            // WER 定为 combase + E_POINTER）猝死，四轮对照实验（托盘服务先释放、Closing
            // 兜底、图标元素摘树、亚克力释放挪到关窗后）均未拦住——失败在纯原生侧，
            // 托管层摸不到。而这一步本来就是多余的：关窗不保存任何状态，下一行就是
            // Environment.Exit。隐藏只是让可见窗口立刻消失，别等进程收尾的间隔。
            GetService<ManagerWindow>().HideWindow();
            Step("退出：管理器已隐藏（不再走原生关窗）。");
        }
        catch (Exception exception)
        {
            Step($"退出：关闭阶段出错（{exception.GetType().Name}），继续收尾。");
        }

        // 托盘在进程收尾前显式释放，让任务栏图标立刻消失（管理器窗口不再关闭，
        // 只是隐藏；图标等不到窗口销毁那一步）。
        _trayIcon?.Dispose();
        _trayIcon = null;
        Step("退出：托盘已释放。");

        // 把门人最后放：收尾期间不允许另一个实例抢跑进来。
        _guard?.Dispose();
        _guard = null;
        Step("退出：单实例守卫已释放。");

        if (relaunch)
        {
            Step(RelaunchSelf() ? "退出：已拉起新进程。" : "退出：拉起新进程失败，重启未完成。");
        }

        Step("退出：准备释放容器。");
        _services?.Dispose();
        _services = null;

        // 到这里便笺、layout、日志都已落盘，容器也已释放——直接结束进程。
        // 不用 Application.Exit()：它会走 WinUI 的整套退出清理面，那里正是
        // 这个应用反复以 exit 139 猝死的地方（2026-10-01 的排查）。
        Environment.Exit(0);
    }

    /// <summary>
    /// 第一段（异步引导）+ 第二段（容器组装）；用户在首次运行向导里放弃时返回
    /// <see langword="null"/>。
    /// </summary>
    private async Task<ServiceProvider?> BuildServicesAsync()
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
            // 首次运行：存储位置交给用户当场决定（§8.6 的首次运行向导）。
            // 不再预置「我的文档\LumiMemo」——在用户不知情的地方凭空建一个目录，
            // 正是「我的便签去哪了」这类问题的源头。向导选完即写盘，这里只管结果。
            string? chosen = await FirstRunWindow.ChooseNotesFolderAsync(settings, settingsStore);
            if (chosen is null)
            {
                return null;
            }
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

        // 设置页要能分辨「当前生效的目录」与「已改、等重启生效的目录」，所以路径对象进容器。
        services.AddSingleton<IAppPaths>(paths);

        // 重启自己（「存储位置」改完生效用）——收尾序列只有 App 知道，由它实现。
        services.AddSingleton<IAppLifecycle>(this);

        // 工具栏那两个「当前值」（文字底色、常用标题级别）：全局一份、随设置落盘，多窗口共享。
        services.AddSingleton<ToolbarPreferences>();
        services.AddSingleton<INoteStorage>(storage);
        services.AddSingleton<ITrashStore>(trash);

        // 换存储位置时把旧目录的便签搬过去（设置页用；向导场景没有旧目录）。
        services.AddSingleton<NotesFolderCopier>();

        // 搜索：当前是本地关键词实现；AI 搜索（语义检索）将实现同一接口，
        // 届时在这里按设置选择注入哪一个即可，UI 层无感知。
        services.AddSingleton<INoteSearchProvider>(new KeywordSearchProvider());
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

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
        });
    }

    /// <summary>崩溃诊断落盘：进程说没就没时，日志文件是唯一留得下的现场。</summary>
    private static void LogCrash(string origin, Exception? exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumiMemo");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{origin}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 写崩溃日志本身绝不能引发新崩溃。
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("XamlUnhandled", e.Exception);
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        LogCrash("AppDomainUnhandled", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        LogCrash("UnobservedTask", e.Exception);
}
