using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace LumiText.Demo;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // 崩溃探针（2026-10-02 排查「启动 ~40s 后 0xc000027b / RO_E_CLOSED 自毁」）：
        // stowed 异常多数会经 WinUI 的 UnhandledException 通道抛出——记录下来并尝试 Handled 存活；
        // 另挂 AppDomain / TaskScheduler 通道兜底。全部写 exe 同目录 crashlog.txt。
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 只记录、不吞掉：Demo 必须响亮崩溃（Handled 保持 false）。
        // 注：本次 0xc000027b/RO_E_CLOSED 链式崩溃从未触发本通道（故障在 COM/包注册层，
        // 不在 XAML 托管异常通道），保留本处理器以备未来的托管侧异常取证。
        CrashLog.Write("Application.UnhandledException", e.Exception);
    }

    /// <summary>最小崩溃日志：追加到 exe 同目录 crashlog.txt（探针专用，定位后移除）。</summary>
    internal static class CrashLog
    {
        public static void Write(string channel, Exception? ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crashlog.txt");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {channel}" + Environment.NewLine +
                    (ex?.ToString() ?? "(null)") + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // 崩溃日志本身绝不能成为新的崩溃源。
            }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // M1/M3 探针路由（Phase 1 设计 §13）：--probe u1/u2/u3 打开对应验证窗口，
        // --probe perf 启动即跑排版基准（结果写 exe 同目录 m3-perf.txt）。
        string? probe = ProbeArgument();
        _window = probe switch
        {
            "u1" => new ScrollPinWindow(),
            "u3" => new VirtualSurfaceWindow(),
            "m4" => new RichDocWindow(),
            "perf" => new DemoWindow(autoRunPerf: true),
            _ => new DemoWindow(autoRunU2: probe == "u2"),
        };
        _window.Activate();
    }

    private static string? ProbeArgument()
    {
        var parts = Environment.GetCommandLineArgs();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "--probe")
            {
                return parts[i + 1].ToLowerInvariant();
            }
        }
        return null;
    }
}
