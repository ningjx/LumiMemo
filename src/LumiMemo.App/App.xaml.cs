using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Logging;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Trash;
using LumiMemo.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App;

/// <summary>
/// 进程生命周期的拥有者，也是唯一的组合根（§4.3）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意不用 Generic Host / <c>IHostedService</c>。Generic Host 的模块化设计
/// 对单进程桌面应用是纯负担，还会把「谁先启动、谁先关闭」的显式顺序藏进扩展方法里。
/// WPF 的 <see cref="Application"/> 已经是进程生命周期的拥有者，
/// 再叠一层 Host 只会产生两套生命周期互相协调的问题。
/// </para>
/// <para>
/// 全部服务注册集中在本文件，不做「按项目分模块注册」的扩展方法——
/// 那会让人无法在一个地方看清整个对象图的形状（§4.3）。
/// </para>
/// <para>
/// <strong>它不写业务逻辑</strong>：启动顺序在 <see cref="StartupSequence"/>，
/// 这里只负责组装对象图、发起启动、以及退出时按 §17.4 收尾。
/// </para>
/// </remarks>
public partial class App : Application
{
    /// <summary>
    /// 第二个实例等回音最多等这么久。
    /// </summary>
    /// <remarks>
    /// 这个延迟直接加在第二个实例的启动上——用户点两下图标，第二下要等这么久才消失。
    /// 足够覆盖「第一个实例正在换管道实例」那几毫秒，又不至于让人以为程序卡住了。
    /// </remarks>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(2);

    private ServiceProvider? _provider;
    private StartupSequence? _startup;
    private SingleInstanceGuard? _singleInstance;
    private TrayService? _tray;
    private IDispatcher? _dispatcher;
    private ManagerViewModel? _manager;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §17.1 第 1 步：建立 %LOCALAPPDATA%\LumiMemo\logs 与 recovery\。
        // 必须在任何日志调用之前完成，否则第一条日志没地方写。
        var paths = new AppPaths();
        paths.EnsureLocalAppDataDirectories();

        _provider = BuildServiceProvider(paths);

        // §17.2：单实例。判在启动序列之前——第二个实例要做的只是通知第一个然后退出，
        // 让它把设置读一遍、把笔记目录扫一遍再扔掉，纯属拿用户的磁盘开玩笑。
        if (!ClaimSingleInstance())
        {
            Shutdown();

            return;
        }

        // 单实例信号要经它们落到界面上。在这里取一次存起来，是因为事件处理器
        // 跑在监听线程上、拿不到局部变量，而在处理器里现取容器等于把组合根
        // 变成一个服务定位器（§4.3）。
        _dispatcher = _provider.GetRequiredService<IDispatcher>();
        _manager = _provider.GetRequiredService<ManagerViewModel>();
        _tray = _provider.GetRequiredService<TrayService>();

        _startup = _provider.GetRequiredService<StartupSequence>();

        // 启动序列是异步的（要读设置、扫磁盘），而 OnStartup 是同步的。
        // 不能阻塞在这里等：那会让 UI 线程在消息泵启动之前就被占住，
        // 而序列里的几个 await 续体恰恰需要消息泵才能回到 UI 线程运行——直接死锁。
        _ = RunStartupAsync(_startup);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序是 §17.4：停自动保存 → flush 未落盘的便签 → 写 layout.json → 释放 Mutex。
        // 这一段必须显式写出来；容器的释放顺序是「与注册顺序相反」，
        // 那和 §17.4 要的顺序毫无关系，只能当最后一道兜底。
        _startup?.ShutdownAndWait();

        // 锁放得比 flush 晚：放早了，另一个实例就能在这一次还没写完 layout 时启动，
        // 两份 layout.json 于是重叠——那正是单实例要防的事。
        _singleInstance?.Dispose();
        _singleInstance = null;

        // 托盘图标同样要在这一步收掉。留着它，用户点了「退出」之后还会看到一个
        // 点得动、但点了没反应的图标，直到鼠标划过才消失。
        _tray?.Dispose();
        _tray = null;

        _provider?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// 认领单实例的那把锁；不是第一个实例时通知已有的那个并返回 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 通知失败<strong>不提示用户</strong>：第二个实例的窗口一闪而过，
    /// 弹一个「已经有一个在跑了」只会让人以为自己按错了。用户再点一次就好。
    /// </remarks>
    private bool ClaimSingleInstance()
    {
        SingleInstanceGuard guard = _provider!.GetRequiredService<SingleInstanceGuard>();

        if (!guard.IsFirstInstance)
        {
            _ = SingleInstanceChannel.TrySignal(SingleInstanceChannel.PipeName, SignalTimeout);

            return false;
        }

        _singleInstance = guard;

        // 先挂事件再开听：反过来的话，启动那一瞬间来的信号会掉在地上——
        // 而「双击图标没反应」正是最难让用户复现再描述清楚的那类问题。
        _singleInstance.SecondInstanceSignalled += OnSecondInstanceSignalled;
        _singleInstance.StartListening();

        return true;
    }

    /// <summary>
    /// 又有人启动了程序：把本实例的便签全部亮出来（§17.2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在监听线程上触发，所以必须先封送到 UI 线程（§3.4 规则 T5）——
    /// <c>ShowAll</c> 要开窗、要改被绑定的 <c>ObservableCollection</c>。
    /// </para>
    /// <para>
    /// <strong>走的必须是「显示全部便签」那一条路</strong>，不能另写一套「把窗口带到前台」。
    /// §17.2 明说了理由：两条路径的行为迟早会不一致，而单实例唤醒是低频操作，
    /// 那种不一致要等到用户抱怨才会被发现。
    /// </para>
    /// </remarks>
    private void OnSecondInstanceSignalled(object? sender, EventArgs e) =>
        _dispatcher?.InvokeAsync(() => _manager?.ShowAllCommand.Execute(null));

    /// <summary>
    /// 发起启动序列，失败时让用户看见并退出进程。
    /// </summary>
    /// <remarks>
    /// <c>async void</c> 在这里是必要的：事件处理器容不下 await。
    /// 唯一的替代是同步阻塞，而那条路会死锁（见 <see cref="OnStartup"/> 里的说明）。
    /// 代价是异常不会自动传播，因此整个方法体必须包在 <c>try</c> 里——
    /// 逃出去的异常在 <c>async void</c> 里会直接把进程掀掉，连提示框都来不及弹。
    /// </remarks>
    private async Task RunStartupAsync(StartupSequence startup)
    {
        try
        {
            await startup.RunAsync();
        }
        catch (Exception ex)
        {
            // 启动失败不能留下一个「只有半个对象图」的僵尸进程：
            // 它既没有窗口也没有托盘，用户只能去任务管理器里结束它。
            IDialogService dialogs = _provider!.GetRequiredService<IDialogService>();

            await dialogs.ShowErrorAsync(
                "LumiMemo 启动失败",
                $"{ex.Message}\n\n程序将退出。");

            Shutdown();
        }
    }

    /// <summary>
    /// 组装整张对象图（§4.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单例的生命周期规则：除 <c>NoteViewModel</c> 之外的一切都是单例。
    /// <c>NoteViewModel</c> 不在这里注册——它由 <see cref="NoteViewModelFactory"/> 手工构造，
    /// 因为它带有必须传入的参数（所属 <c>Note</c> 与所属窗口），
    /// 交给容器解析只会得到「构造参数从哪来」的假问题（§4.3）。
    /// </para>
    /// <para>
    /// <strong>几个接口与实现同时注册</strong>（<c>AddSingleton&lt;TImpl&gt;</c> 后跟
    /// <c>AddSingleton&lt;TInterface&gt;(sp =&gt; sp.GetRequiredService&lt;TImpl&gt;())</c>）：
    /// 消费方按接口取，而 <see cref="StartupSequence"/> 需要那些「只在具体类型上有」的可写属性
    /// （默认宽度、默认颜色）。两条注册指向同一个实例，不能各注册一次。
    /// </para>
    /// </remarks>
    private static ServiceProvider BuildServiceProvider(AppPaths paths)
    {
        var services = new ServiceCollection();

        // ---- 日志 ----
        // 落到 %LOCALAPPDATA%\LumiMemo\logs\（§20.5）。注册成容器里的 ILoggerProvider
        // 而不是在这里直接 AddProvider(实例)：这样释放它的就是容器，而容器的释放在
        // ShutdownAndWait 之后——退出前那几条日志因此还能被写下去。
        //
        // 不挂控制台/调试输出：用户双击 exe 时那两处都看不见，等于没写。
        services.AddLogging(builder => builder.Services.AddSingleton<ILoggerProvider>(
            sp => new FileLoggerProvider(
                paths.LogDirectory,
                sp.GetRequiredService<IClock>())));

        // ---- 环境 ----
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<AtomicFileWriter>();
        services.AddSingleton<IUiTimerFactory>(
            new DispatcherTimerFactory(Current.Dispatcher));
        services.AddSingleton<IDispatcher, WpfDispatcher>();

        // 退出请求抽成接口只为一件事：托盘菜单的「退出」在生产实现里会真的把测试进程关掉。
        // 生产实现只调 Application.Shutdown()，收尾一律交给 OnExit（见该类的说明）。
        services.AddSingleton<IApplicationLifetime, WpfApplicationLifetime>();

        // 同时注册具体类型与接口：设置载入流程需要调用 SetNotesFolder / SetAttachmentsFolderName，
        // 那两个是具体类型上的方法，不属于只读的 IAppPaths。
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);

        // ---- 显示器（§13.8）----
        services.AddSingleton<MonitorEnumerator>();
        services.AddSingleton<IDisplayProvider>(sp => sp.GetRequiredService<MonitorEnumerator>());

        // ---- 存储 ----
        services.AddSingleton<JsonSettingsStore>();
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());

        services.AddSingleton<JsonLayoutStore>();
        services.AddSingleton<ILayoutStore>(sp => sp.GetRequiredService<JsonLayoutStore>());

        services.AddSingleton<MarkdownNoteRepository>();
        services.AddSingleton<INoteRepository>(sp => sp.GetRequiredService<MarkdownNoteRepository>());

        // 回收站与笔记同卷（§8.1），所以它跟笔记目录绑在一起，而不是跟设备状态绑在一起。
        services.AddSingleton<FileSystemTrashStore>();
        services.AddSingleton<ITrashStore>(sp => sp.GetRequiredService<FileSystemTrashStore>());

        // ---- 状态 ----
        // NoteStore 与 SearchIndex 都是纯内存状态，构造无依赖。
        services.AddSingleton<NoteStore>();
        services.AddSingleton<SearchIndex>();

        // ---- 核心业务 ----
        services.AddSingleton<LayoutService>();

        // TrashService 必须排在 NoteService 前面：后者依赖它（删除与恢复都转发过去）。
        // 容器其实不在意注册顺序，但读这一节的人在意。
        services.AddSingleton<TrashService>();
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<AutoSaveService>();

        // ---- 界面基础设施 ----
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFolderPicker, FolderPickerDialog>();
        services.AddSingleton<IShellLauncher, ExplorerShellLauncher>();

        // 注册的是 WeakReferenceMessenger.Default，因此运行时行为与直接引用那个静态单例
        // 完全一致，但依赖关系是显式的：测试里可以注入一条全新的总线，
        // 用例之间不会通过全局状态互相干扰。
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // 同时注册具体类型与接口：StartupSequence 要往 RestoreAfterShowDesktop 上推设置值，
        // 那是具体类型上的属性，不属于 IWindowManager（与上面几个「两个注册指向同一实例」同理）。
        services.AddSingleton<WindowManager>();
        services.AddSingleton<IWindowManager>(sp => sp.GetRequiredService<WindowManager>());
        services.AddSingleton<NoteViewModelFactory>();

        // ---- 界面 ----
        // ManagerViewModel 与 ManagerWindow 都是单例：整个进程只有一个管理器。
        services.AddSingleton<ManagerViewModel>();
        services.AddSingleton<ManagerWindow>();

        // SettingsWindow 则是「每次打开一个新的」，不能注册成单例：
        // WPF 的 Window 一旦 Close() 过就不能再 Show()，第二次会抛异常。
        // 「同时只能开一个」这条约束由 SettingsWindowLauncher 记着。
        //
        // 工厂委托是刻意的：SettingsWindowLauncher 若直接注入 IServiceProvider，
        // 它会退化成一个谁也看不清依赖的服务定位器。注册顺序上它必须排在
        // StartupSequence 之后——SettingsViewModel 依赖 ISettingsApplier，
        // 而它的实现是 StartupSequence，读起来才是一条向下的链。
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton(sp => new SettingsWindowLauncher(
            sp.GetRequiredService<SettingsWindow>));

        // ---- 单实例与托盘 ----
        // 名字从 SingleInstanceChannel 取（它带着当前用户的 SID），这里显式写一个
        // 工厂委托，好让「这个名字是谁定的」在注册处一眼可见。
        services.AddSingleton(sp => new SingleInstanceGuard(
            SingleInstanceChannel.MutexName,
            SingleInstanceChannel.PipeName,
            sp.GetRequiredService<ILogger<SingleInstanceGuard>>()));

        // 托盘的两个对象都只建一份，而且必须是同一份：TrayService 建图标、挂菜单，
        // TrayViewModel 是菜单那头；分两次注册的话，菜单项指向的会是一份
        // 从没被灌过设置、也不知道管理器在哪的空壳。它们同时也被
        // StartupSequence（灌设置、启动）与 App.OnExit（收图标）取用。
        // 工厂委托是刻意的：ManagerViewModel 也要用它（一张便签都没打开时把自己带出来），
        // 而直接注入 ManagerWindow 会形成 ManagerWindow → ManagerViewModel →
        // ManagerWindowPresenter → ManagerWindow 的环。工厂是惰性的，那条边在构造期就断了。
        services.AddSingleton<IManagerWindowPresenter>(
            sp => new ManagerWindowPresenter(() => sp.GetRequiredService<ManagerWindow>()));
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayService>();

        // ---- 启动编排 ----
        services.AddSingleton<StartupSequence>();

        // 设置窗口保存后靠它把值推下去。指向上面那同一个实例——两个注册各建一份的话，
        // 「设置窗口里改了立刻生效」推的会是一个从没跑过启动、内部全是空状态的对象。
        services.AddSingleton<ISettingsApplier>(sp => sp.GetRequiredService<StartupSequence>());

        // ValidateOnBuild：建容器时就把整张对象图走一遍，缺哪个注册当场抛出来。
        // 这不是多余的谨慎——「接口写了、实现写了、注册漏了」在这个仓库里真实发生过一次
        // （IApplicationLifetime），而它在运行时的表现是双击图标毫无反应、
        // 事件日志里连一条记录都没有，排查成本远高于这里多跑的几毫秒。
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
