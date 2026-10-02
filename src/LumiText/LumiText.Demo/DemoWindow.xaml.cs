using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using LumiText.Core.Documents;
using LumiText.Core.Layout;
using WinRT;

namespace LumiText.Demo;

/// <summary>
/// S2 演示窗口：毛玻璃外壳 + FloatWrapView。
/// 亚克力参数与主程序一致（IsInputActive=true，失焦玻璃不降级）。
/// </summary>
public sealed partial class DemoWindow : Window
{
    private readonly DesktopAcrylicController? _backdropController;
    private readonly List<double> _frameMs = new();
    private bool _documentSet;
    private long _lastFrameTicks;

    public DemoWindow()
    {
        InitializeComponent();

        if (DesktopAcrylicController.IsSupported())
        {
            _backdropController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base,
                TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
                TintOpacity = 0.34f,
                LuminosityOpacity = 0.58f,
                FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248),
            };
            _backdropController.AddSystemBackdropTarget(
                this.As<ICompositionSupportsSystemBackdrop>());
            _backdropController.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Light,
            });
        }
        else
        {
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
        }

        WrapView.LayoutStatsChanged += ms =>
            StatsText.Text = $"排版耗时：{ms:F2} ms（全量）";
        DebugToggle.Checked += (_, _) => { WrapView.DebugOverlay = true; WrapView.Relayout(); };
        DebugToggle.Unchecked += (_, _) => { WrapView.DebugOverlay = false; WrapView.Relayout(); };
        WrapView.SizeChanged += OnWrapViewSizeChanged;

        Closed += (_, _) => _backdropController?.Dispose();
    }

    private void OnWrapViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_documentSet || e.NewSize.Width < 200)
        {
            return;
        }
        _documentSet = true;

        // 初始浮动块：左上一块、右中一块（X 随窗口宽度定位）。
        float rightX = (float)Math.Max(260, e.NewSize.Width - 190);
        WrapView.SetDocument(
            BuildParagraphs(),
            new[]
            {
                new FloatObject(1, new LayoutRect(24, 48, 150, 110), FloatSide.Left, Margin: 8f),
                new FloatObject(2, new LayoutRect(rightX, 260, 150, 120), FloatSide.Right, Margin: 8f),
            });
    }

    // ------------------------------------------------------------------
    // S2 验收：性能测量（设计文档 §3.6）
    // ------------------------------------------------------------------

    private void OnPerfButtonClick(object sender, RoutedEventArgs e)
    {
        PerfButton.IsEnabled = false;
        StatsText.Text = "测量中：跑 [A] 全量排版与 [B] 拖动逐帧重排…";
        try
        {
            var (report, summary) = PerfBenchmark.RunLayoutBenchmarks();
            PerfBenchmark.Append($"排版性能（{DateTime.Now:yyyy-MM-dd HH:mm}）", report);
            StatsText.Text = summary;
        }
        catch (Exception ex)
        {
            StatsText.Text = $"性能验收失败：{ex.Message}";
        }
        finally
        {
            PerfButton.IsEnabled = true;
        }
    }

    private void OnFrameRecChecked(object sender, RoutedEventArgs e)
    {
        _frameMs.Clear();
        _lastFrameTicks = 0;
        CompositionTarget.Rendering += OnCompositionRendering;
        StatsText.Text = "帧率记录中——请连续拖动紫色浮动块，拖动结束后关闭开关";
    }

    private void OnFrameRecUnchecked(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        string summary = PerfBenchmark.FrameStats(_frameMs);
        PerfBenchmark.Append($"拖动帧率（{DateTime.Now:yyyy-MM-dd HH:mm}，{Environment.OSVersion.VersionString}）", summary);
        StatsText.Text = $"拖动帧率：{summary}";
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            _frameMs.Add((now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency);
        }
        _lastFrameTicks = now;
    }

    private static ParagraphBlock[] BuildParagraphs()
    {
        const string p1 =
            "LumiText 是一个为毛玻璃窗口而生的文本渲染内核。它不依赖任何宿主产品代码，" +
            "排版引擎与渲染层完全分离：引擎在纯计算空间工作，渲染层只负责把行盒画到 Composition 表面。" +
            "拖动左上角或右侧的紫色浮动块，观察文字如何实时让位、环绕，并在浮动块越过段落时" +
            "从一侧飞到另一侧。The quick brown fox jumps over the lazy dog. " +
            "排版的核心思想很朴素：所有浮动块的顶缘与底缘把文档纵向切成若干条带，" +
            "每条带内的可用宽度是恒定的；文字按带逐行填充，行高跨越浮动块边缘时自动取最窄约束。";
        const string p2 =
            "这一行为与 Word 的杂志式环绕一致，但实现完全自研——DirectWrite 本身并不提供浮动排除区，" +
            "它只负责在给定宽度内断行；把「可用宽度」按浮动矩形切成若干段，就是环绕的全部秘密。" +
            "同一条带内若有多个段（图片左右两侧），行盒共享基线，文字在图片两侧对齐如常。" +
            "1234567890 数字与 English 混排也能正确断行。拖动时的每一帧都是一次真实的全量重排，" +
            "底部状态栏显示本次重排耗时，供性能验收参考。";
        const string p3 =
            "下一步（Phase 1）：文档模型接入真实便签内容、多样式 run、行内图片对象与滚动虚拟化；" +
            "Phase 2 补齐编辑层（光标、选区、撤销、TSF 输入法）；Phase 3 实现待办复选框、标题层级与排版动画。";
        return new[]
        {
            new ParagraphBlock(p1, SpaceAfter: 10f),
            new ParagraphBlock(p2, SpaceAfter: 10f),
            new ParagraphBlock(p3),
        };
    }
}
