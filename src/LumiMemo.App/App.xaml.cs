using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using Microsoft.Extensions.DependencyInjection;

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
/// </remarks>
public partial class App : Application
{
    private ServiceProvider? _provider;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §17.1 第 1 步：建立 %LOCALAPPDATA%\LumiMemo\logs 与 recovery\。
        // 必须在任何日志调用之前完成，否则第一条日志没地方写。
        var paths = new AppPaths();
        paths.EnsureLocalAppDataDirectories();

        _provider = BuildServiceProvider(paths);

        // ------------------------------------------------------------------
        // 骨架到此为止：DI 能组装、进程能起来、能干净退出。
        //
        // §17.1 的启动序列余下部分按顺序接在这里：
        //   2. 载入 settings.json（ISettingsStore）→ 拿到笔记目录
        //   3. AppPaths.SetNotesFolder / SetAttachmentsFolderName
        //   4. 载入 layout.json（ILayoutStore），剔除已不存在显示器的坐标
        //   5. 扫描笔记目录填充 NoteStore，建立 FileSystemWatcher
        //   6. 按 layout 恢复上次打开的便签窗口
        //   7. 创建托盘图标、注册全局热键
        //
        // 在那之前显式退出：留一个既没有窗口也没有托盘的进程在后台，
        // 用户只能去任务管理器里结束它。
        // ------------------------------------------------------------------
        Shutdown();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // 注意：这里**不能**只靠 _provider.Dispose() 来完成退出收尾。
        // 容器释放单例的顺序是「与注册顺序相反」，那和 §17.4 要求的退出顺序毫无关系——
        // 正确顺序是：停自动保存 → flush 未落盘的改动 → 关 FileSystemWatcher
        //            → 写 layout.json → 关窗口 → 退托盘图标 → flush 日志。
        // 这个顺序必须显式写出来；容器释放只作为最后一道兜底。
        _provider?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// 组装整张对象图（§4.3）。
    /// </summary>
    /// <remarks>
    /// 单例的生命周期规则：除 <c>NoteViewModel</c> 之外的一切都是单例。
    /// <c>NoteViewModel</c> 不在这里注册——它由 <c>NoteViewModelFactory</c> 手工构造，
    /// 因为它带有必须传入的参数（所属 <c>Note</c> 与所属窗口），
    /// 交给容器解析只会得到「构造参数从哪来」的假问题（§4.3）。
    /// </remarks>
    private static ServiceProvider BuildServiceProvider(AppPaths paths)
    {
        var services = new ServiceCollection();

        // ---- 环境 ----
        services.AddSingleton<IClock, SystemClock>();

        // 同时注册具体类型与接口：设置载入流程需要调用 SetNotesFolder / SetAttachmentsFolderName，
        // 那两个是具体类型上的方法，不属于只读的 IAppPaths。
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);

        // ---- 状态 ----
        // NoteStore 与 SearchIndex 都是纯内存状态，构造无依赖，注册为单例即可。
        services.AddSingleton<NoteStore>();
        services.AddSingleton<SearchIndex>();

        // ---- 消息总线 ----
        // 注册的是 WeakReferenceMessenger.Default，因此运行时行为与直接引用那个静态单例
        // 完全一致，但依赖关系是显式的：测试里可以注入一条全新的总线，
        // 用例之间不会通过全局状态互相干扰。
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        return services.BuildServiceProvider();
    }
}
