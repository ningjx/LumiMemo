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
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
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
    private ServiceProvider? _provider;
    private StartupSequence? _startup;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §17.1 第 1 步：建立 %LOCALAPPDATA%\LumiMemo\logs 与 recovery\。
        // 必须在任何日志调用之前完成，否则第一条日志没地方写。
        var paths = new AppPaths();
        paths.EnsureLocalAppDataDirectories();

        _provider = BuildServiceProvider(paths);

        _startup = _provider.GetRequiredService<StartupSequence>();

        // 管理器是程序的落脚点：关掉它才退出进程（§17.3）。
        // 便签窗口全部关光不会退出——管理器还在。
        //
        // 本轮的临时语义：还没有托盘，所以关掉管理器就是真退出。
        // 托盘接上后这里改成「收进托盘」，真正的退出走托盘菜单。
        ManagerWindow manager = _provider.GetRequiredService<ManagerWindow>();
        manager.Closed += (_, _) => Shutdown();

        // 启动序列是异步的（要读设置、扫磁盘），而 OnStartup 是同步的。
        // 不能阻塞在这里等：那会让 UI 线程在消息泵启动之前就被占住，
        // 而序列里的几个 await 续体恰恰需要消息泵才能回到 UI 线程运行——直接死锁。
        _ = RunStartupAsync(_startup);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序是 §17.4：停自动保存 → flush 未落盘的便签 → 写 layout.json。
        // 这一段必须显式写出来；容器的释放顺序是「与注册顺序相反」，
        // 那和 §17.4 要的顺序毫无关系，只能当最后一道兜底。
        _startup?.ShutdownAndWait();

        _provider?.Dispose();

        base.OnExit(e);
    }

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
        // 暂时不挂 provider：控制台与调试输出在用户双击 exe 时都看不见，
        // 而写到文件需要一套带滚动的文件日志器，那是独立的一块工作。
        // 现在挂一个半成品只会掩盖真正的启动问题。
        // 注意：这不影响正确性——所有 ILogger 调用都照常执行，只是没人接收。
        services.AddLogging();

        // ---- 环境 ----
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<AtomicFileWriter>();
        services.AddSingleton<IUiTimerFactory>(
            new DispatcherTimerFactory(Current.Dispatcher));
        services.AddSingleton<IDispatcher, WpfDispatcher>();

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

        // ---- 状态 ----
        // NoteStore 与 SearchIndex 都是纯内存状态，构造无依赖。
        services.AddSingleton<NoteStore>();
        services.AddSingleton<SearchIndex>();

        // ---- 核心业务 ----
        services.AddSingleton<LayoutService>();
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<AutoSaveService>();

        // ---- 界面基础设施 ----
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFolderPicker, FolderPickerDialog>();

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

        // ---- 启动编排 ----
        services.AddSingleton<StartupSequence>();

        return services.BuildServiceProvider();
    }
}
