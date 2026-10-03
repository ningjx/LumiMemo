using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using LumiText.Core.Documents;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;
using Windows.UI;

namespace LumiText.Demo;

/// <summary>
/// M4 探针：块驱动渲染的端到端验证（Phase 1 设计 §6）——
/// 标题层级、行内样式 run、Todo 矢量复选框（悬挂缩进/换行对齐/双态）、
/// Divider 线、浮动图片（锚定 + 直给两种定位）在同一份 Document 上一次渲染。
/// 验收方式：截图目视（验收记录 RESULTS-Phase1.md §7）。
/// </summary>
public sealed partial class RichDocWindow : Window
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);

    private readonly DesktopAcrylicController? _backdrop;
    private readonly CompositionTextSurface _surface = new();
    private readonly FlowDocumentRenderer _renderer = new(new Win2DTextMeasurer());
    private readonly DocumentImageStore _imageStore = new(CanvasDevice.GetSharedDevice());
    private readonly Document _document = BuildDocument();
    private bool _documentSet;

    public RichDocWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 640));

        _surface.RenderContent = OnRenderContent;
        Activated += OnFirstActivated;
        SurfaceHost.SizeChanged += (_, _) => Relayout();
        Closed += (_, _) =>
        {
            _surface.Dispose();
            _imageStore.Dispose();
            _backdrop?.Dispose();
        };
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _surface.Attach(SurfaceHost);
        _renderer.Images = _imageStore;
        Relayout();
        // §8.1：解码与排版并行——文档载入即排版上屏，位图就绪后补画。
        _ = WarmupImagesAsync();
    }

    private async Task WarmupImagesAsync()
    {
        await _imageStore.WarmupAsync(
            _document.Images,
            () => DispatcherQueue.TryEnqueue(() => _surface.Invalidate()));

        // M5 解码实测（§10.3）：首轮含 WIC/Win2D 管线初始化（冷），再跑一轮取稳态（热）。
        using var warmStore = new DocumentImageStore(CanvasDevice.GetSharedDevice());
        await warmStore.WarmupAsync(_document.Images);

        var report = new System.Text.StringBuilder();
        report.AppendLine("    首轮（冷，含管线初始化）:");
        foreach (var (id, bytes, ms) in _imageStore.DecodeLog)
        {
            report.AppendLine($"    {id}　{bytes / 1024.0:F0} KB　{ms:F2} ms");
        }
        report.AppendLine("    二轮（热，稳态）:");
        foreach (var (id, bytes, ms) in warmStore.DecodeLog)
        {
            report.AppendLine($"    {id}　{bytes / 1024.0:F0} KB　{ms:F2} ms");
        }
        PerfBenchmark.Append(
            $"M5 图片解码实测（{DateTime.Now:yyyy-MM-dd HH:mm}，{Environment.OSVersion.VersionString}，Release）",
            report.ToString(), "m5-perf.txt");
    }

    private void Relayout()
    {
        if (SurfaceHost.ActualWidth < 200)
        {
            return;
        }
        _renderer.UpdateLayout(_document, (float)SurfaceHost.ActualWidth);
        _documentSet = true;
        _surface.Invalidate();
    }

    private void OnRenderContent(CanvasDrawingSession session, Vector2 sizeDips, float scale)
    {
        if (!_documentSet || _renderer.Current is null)
        {
            return;
        }
        session.Transform = Matrix3x2.CreateScale(scale);
        _renderer.Render(session, _renderer.Current, InkColor, debugOverlay: false);
    }

    /// <summary>验收文档：覆盖 §6.2 全部块级渲染路径 + §6.3 锚定浮动 + §8 真实图片。</summary>
    private static Document BuildDocument()
    {
        var accent = new Color32(255, 180, 40, 60);
        return new Document(
            new Block[]
            {
                new HeadingBlock("LumiText 块级渲染验收", 1, spaceAfter: 8f),
                new ParagraphBlock(
                    new TextRun[]
                    {
                        new("行内样式："),
                        new("粗体", new InlineStyle(Bold: true)),
                        new("、"),
                        new("斜体", new InlineStyle(Italic: true)),
                        new("、"),
                        new("下划线", new InlineStyle(Underline: true)),
                        new("、"),
                        new("删除线", new InlineStyle(Strikethrough: true)),
                        new("、"),
                        new("彩色", new InlineStyle(Color: accent)),
                        new("与"),
                        new("大字", new InlineStyle(FontSizeRatio: 1.4f)),
                        new("混排。The quick brown fox jumps over the lazy dog."),
                    },
                    spaceAfter: 10f),
                new HeadingBlock("待办与分割线", 3, spaceAfter: 6f),
                new TodoBlock("未勾选的短待办"),
                new TodoBlock(
                    "已勾选的长待办——验证换行后文本与首行文本左缘对齐（悬挂缩进）：这是一段足够长的" +
                    "待办内容，需要换行才能放得下，第二行应当与第一行的文字左缘对齐而不是对齐复选框。",
                    @checked: true),
                new DividerBlock(),
                new ParagraphBlock(
                    "分割线之上是待办区。右侧是「锚定到本文块首 + 偏移」的浮动图片（§6.3 两遍排版解析），" +
                    "左侧直给矩形定位的是另一张浮动图（983KB PNG，解码并行预热）。文字应当在两者之间" +
                    "自然环绕流动，验证真实位图渲染与两种浮动定位路径在同一份文档里共存。"),
                // 锚定浮动：锚到本段（块 5）首字符（字符级锚定，两遍排版解析）
                new ImageBlock("img-anchored", 150, 100,
                    new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(5, 0))),
                // 直给浮动：矩形直给路径（S2 现状）
                new ImageBlock("img-direct", 150, 113,
                    new FloatPlacement(FloatSide.Left, 8f, Position: new FloatPosition(0, 230))),
            },
            LoadImageResources());
    }

    /// <summary>从 Assets 读测试图片为 ImageResource（tools/gen_test_images.py 生成）。</summary>
    private static ImageResource[] LoadImageResources()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        return new[]
        {
            new ImageResource("img-anchored", "image/png",
                File.ReadAllBytes(Path.Combine(assets, "test-img-gradient.png"))),
            new ImageResource("img-direct", "image/png",
                File.ReadAllBytes(Path.Combine(assets, "test-img-noise.png"))),
        };
    }
}
