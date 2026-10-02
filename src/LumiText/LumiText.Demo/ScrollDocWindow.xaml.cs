using System.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using LumiText.Core.Documents;

namespace LumiText.Demo;

/// <summary>
/// M6 探针：300+ 行长文档的滚动虚拟化验收（Phase 1 设计 §9.2 末行用例）——
/// 滚动流畅、无上屏残影、origin 回滚后内容完整；快速甩动允许短暂透玻璃、停止后下一帧完整。
/// 自动序列（截图锚点）：t+2s 滚到 45%，t+4s 滚到底，t+6s 滚回顶（验证回滚完整），
/// t+7s 写重绘基准（§10.3 一屏重绘耗时）到 m6-perf.txt。
/// </summary>
public sealed partial class ScrollDocWindow : Window
{
    private readonly DesktopAcrylicController? _backdrop;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<double> _redrawSamples = new();
    private int _stage;

    public ScrollDocWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 640));

        DocView.SetDocument(BuildLongDocument());

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += OnTimerTick;

        Activated += OnFirstActivated;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _backdrop?.Dispose();
        };
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, object e)
    {
        _stage++;
        switch (_stage)
        {
            case 1:
                DocView.ChangeView(null, DocView.ScrollableHeight * 0.45, null, disableAnimation: true);
                break;
            case 2:
                DocView.ChangeView(null, DocView.ScrollableHeight, null, disableAnimation: true);
                break;
            case 3:
                DocView.ChangeView(null, 0, null, disableAnimation: true);
                break;
            default:
                _timer.Stop();
                WriteRedrawBenchmark();
                break;
        }
    }

    /// <summary>§10.3「一屏可见区重绘耗时」：同步强制 13 次整面重绘（前 3 次热身，取后 10 次）。</summary>
    private void WriteRedrawBenchmark()
    {
        for (int i = 0; i < 13; i++)
        {
            DocView.InvalidateSurface();
            if (i >= 3)
            {
                _redrawSamples.Add(DocView.LastRedrawMs);
            }
        }
        var sorted = _redrawSamples.OrderBy(v => v).ToArray();
        string report =
            $"文档 {DocView.ScrollableHeight + DocView.ActualHeight:F0} dip（≈{DocView.ScrollableHeight / DocView.ActualHeight:F1} 屏）；10 次整面（3 屏区）重绘\n" +
            $"    中位 {sorted[5]:F3} ms　P95 {sorted[(int)((sorted.Length - 1) * 0.95)]:F3} ms　最大 {sorted[^1]:F3} ms\n" +
            $"    口径：origin 移动触发的整面重绘；视口在表面覆盖区间内的滚动重绘成本为 0";
        PerfBenchmark.Append(
            $"M6 可见区重绘基准（{DateTime.Now:yyyy-MM-dd HH:mm}，{Environment.OSVersion.VersionString}，Release）",
            report, "m6-perf.txt");
    }

    /// <summary>≈350 行混合内容长文档（标题/段落/待办/分割线/双浮动图片）。</summary>
    private static Document BuildLongDocument()
    {
        var blocks = new List<Block>
        {
            new HeadingBlock("M6 滚动虚拟化验收文档", 1, spaceAfter: 8f),
            new ParagraphBlock("本文档约 350 行：滚动应流畅无残影；origin 回滚后内容完整；" +
                "快速甩动允许短暂透出玻璃底，停止后下一帧内容完整。", spaceAfter: 12f),
            new ImageBlock("img-anchored", 150, 100,
                new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(1, 8f, 24f))),
        };
        for (int s = 1; s <= 26; s++)
        {
            blocks.Add(new HeadingBlock($"第 {s} 节", 2, spaceAfter: 4f));
            blocks.Add(new ParagraphBlock(
                $"第 {s} 节第一段。排版引擎把可用宽度按浮动矩形切成若干段，文字按段填充；" +
                "The quick brown fox jumps over the lazy dog. 行高跨越浮动边缘时取所跨各带段集合的交集，" +
                "矮图片只挤占相交的那几行。同一条带内若有多个段，行盒共享基线，文字在图片两侧对齐如常。" +
                "滚动虚拟化只把视口 ±1 屏画进虚拟 surface，其余区域不占显存。", spaceAfter: 8f));
            blocks.Add(new ParagraphBlock(
                $"第 {s} 节第二段。钉住与重绘由滚动偏移驱动：视口落在表面覆盖区间内时滚动零成本；" +
                "越出时移动 origin 并整面重绘。1234567890 数字与 English 混排同样正确断行。" +
                "长文档是便签的兜底场景，常态短文档走整面覆盖的退避路径，只画一次。", spaceAfter: 8f));
            blocks.Add(new TodoBlock($"第 {s} 节待办事项（{(s % 2 == 0 ? "已完成" : "待办")}）", @checked: s % 2 == 0));
            blocks.Add(new DividerBlock());
        }
        blocks.Add(new ImageBlock("img-direct", 150, 113,
            new FloatPlacement(FloatSide.Left, 8f, Position: new FloatPosition(0, 2400))));
        return new Document(blocks, LoadImages());
    }

    private static ImageResource[] LoadImages()
    {
        string assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
        return new[]
        {
            new ImageResource("img-anchored", "image/png",
                System.IO.File.ReadAllBytes(System.IO.Path.Combine(assets, "test-img-gradient.png"))),
            new ImageResource("img-direct", "image/png",
                System.IO.File.ReadAllBytes(System.IO.Path.Combine(assets, "test-img-noise.png"))),
        };
    }
}
