using System.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using LumiText.Core.Documents;
using Windows.UI;

namespace LumiText.Demo;

/// <summary>
/// M7 探针：视觉回归对照（Phase 1 设计 §9.2）——左 RichEditBox（现产品同款外观 +
/// 手写 RTF）与右 LumiDocumentView（同内容新模型）在同一 Acrylic 配置下逐用例比对。
/// 前置确认：RichEditBox 默认字号实测写入 m7-report.txt（TextStyle.Default 已按现产品定 14dip）。
/// t+2.5s 双侧同翻到第二屏（截图锚点：首屏用例 1–4，次屏用例 5–6）。
/// </summary>
public sealed partial class CompareWindow : Window
{
    private static readonly Color FrostColor = Color.FromArgb(0x2E, 0xF7, 0xF3, 0xFD);   // 现产品 EditorFrost
    private static readonly Color InkColor = Color.FromArgb(255, 48, 43, 57);             // 现产品正文墨色

    private readonly DesktopAcrylicController? _backdrop;
    private readonly DispatcherQueueTimer _timer;

    public CompareWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 680));

        // 现产品 RichEditorHost 的同款资源覆盖（背景/边框全走 frost/透明）。
        Editor.Background = new SolidColorBrush(FrostColor);
        Editor.Foreground = new SolidColorBrush(InkColor);
        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
        })
        {
            Editor.Resources[key] = new SolidColorBrush(FrostColor);
        }
        foreach (string key in new[]
        {
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        })
        {
            Editor.Resources[key] = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
        }
        RightFrost.Background = new SolidColorBrush(FrostColor);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(2500);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            // 双侧同翻到第二屏（用例 5–6 截图锚点）。
            var editorViewer = FindDescendantScrollViewer(Editor);
            editorViewer?.ChangeView(null, 420, null, disableAnimation: true);
            DocView.ChangeView(null, 420, null, disableAnimation: true);
        };

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
        // 先喂内容再锁只读——ReadOnly 的 RichEditBox 调 SetText 会抛 UnauthorizedAccessException。
        Editor.IsReadOnly = false;
        Editor.Document.SetText(TextSetOptions.FormatRtf, ComparisonRtf);
        Editor.IsReadOnly = true;
        DocView.SetDocument(BuildComparisonDocument());
        _timer.Start();

        // 前置确认（§9.2）：RichEditBox 默认字号实测（= 现产品字号，产品未显式设置）。
        var report = new StringBuilder();
        report.AppendLine($"RichEditBox 默认 FontSize 实测：{Editor.FontSize} dip（现产品未显式设置，取框架默认）");
        report.AppendLine($"TextStyle.Default：{TextStyle.Default.FontFamily} {TextStyle.Default.FontSize} dip（§9.2 前置确认：以现产品为准）");
        report.AppendLine("RTF 字号基准：\\fs21 = 10.5pt = 14 dip（与实测默认一致时对照前提成立）");
        report.AppendLine("对照布局：双侧同 frost 底（A=0x2E 淡紫白）、同墨色 (48,43,57)、同内边距 (18,16,18,16)、同列宽");
        PerfBenchmark.Append(
            $"M7 对照前置确认（{DateTime.Now:yyyy-MM-dd HH:mm}，{Environment.OSVersion.VersionString}）",
            report.ToString(), "m7-report.txt");
    }

    private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.ScrollViewer viewer)
            {
                return viewer;
            }
            var found = FindDescendantScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>对照 RTF（手写，§9.2 不引入 RTF 生成器）：\fs 为半磅，21=10.5pt=14dip、33=22dip、27=18dip、24=16dip。</summary>
    private const string ComparisonRtf = """
        {\rtf1\ansi\ansicpg936\deff0{\fonttbl{\f0\fnil Segoe UI;}}
        \f0\fs21 纯文本长段落：排版引擎把可用宽度按浮动矩形切成若干段，文字按段填充；The quick brown fox jumps over the lazy dog. 行高跨越浮动边缘时取所跨各带段集合的交集，矮图片只挤占相交的那几行。\par
        样式混排：\b 粗体\b0、\i 斜体\i0、\ul 下划线\ulnone、\strike 删除线\strike0，与常规文字同段落混排不间断行。\par
        \fs33 标题一\fs21\par
        \fs27 标题二\fs21\par
        \fs24 标题三\fs21\par
        \u9744? 未勾选待办一\par
        \u9745? 已勾选待办二（换行长待办：这是一段足够长的待办内容，需要换行才能放得下，观察第二行与第一行文字左缘的对齐方式）\par
        \u9744? 未勾选待办三\par
        \u9745? 已勾选待办四\par
        \u9744? 未勾选待办五\par
        \u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\u9472?\par
        结尾段落：分割线之上是待办区。右侧新渲染器独有浮动图片环绕能力（现产品无此能力，单独验收），此段继续加长以形成第二屏内容：排版的核心思想很朴素，所有浮动块的顶缘与底缘把文档纵向切成若干条带，每条带内的可用宽度是恒定的，文字按带逐行填充。\par
        }
        """;

    /// <summary>与 RTF 等价的新模型文档。</summary>
    private static Document BuildComparisonDocument()
    {
        const string plain =
            "纯文本长段落：排版引擎把可用宽度按浮动矩形切成若干段，文字按段填充；" +
            "The quick brown fox jumps over the lazy dog. 行高跨越浮动边缘时取所跨各带段集合的交集，矮图片只挤占相交的那几行。";
        const string longTodo =
            "已勾选待办二（换行长待办：这是一段足够长的待办内容，需要换行才能放得下，观察第二行与第一行文字左缘的对齐方式）";
        const string ending =
            "结尾段落：分割线之上是待办区。右侧新渲染器独有浮动图片环绕能力（现产品无此能力，单独验收），" +
            "此段继续加长以形成第二屏内容：排版的核心思想很朴素，所有浮动块的顶缘与底缘把文档纵向切成若干条带，" +
            "每条带内的可用宽度是恒定的，文字按带逐行填充。";

        return new Document(
            new Block[]
            {
                new ParagraphBlock(plain, spaceAfter: 10f),
                new ParagraphBlock(
                    new TextRun[]
                    {
                        new("样式混排："),
                        new("粗体", new InlineStyle(Bold: true)),
                        new("、"),
                        new("斜体", new InlineStyle(Italic: true)),
                        new("、"),
                        new("下划线", new InlineStyle(Underline: true)),
                        new("、"),
                        new("删除线", new InlineStyle(Strikethrough: true)),
                        new("，与常规文字同段落混排不间断行。"),
                    },
                    spaceAfter: 10f),
                new HeadingBlock("标题一", 1, spaceAfter: 10f),
                new HeadingBlock("标题二", 2, spaceAfter: 10f),
                new HeadingBlock("标题三", 3, spaceAfter: 10f),
                new TodoBlock("未勾选待办一"),
                new TodoBlock(longTodo, @checked: true),
                new TodoBlock("未勾选待办三"),
                new TodoBlock("已勾选待办四", @checked: true),
                new TodoBlock("未勾选待办五", spaceAfter: 6f),
                new DividerBlock(),
                new ParagraphBlock(ending),
                new ImageBlock("img-compare", 150, 100,
                    new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(10, 8f, 12f))),
            },
            LoadImages());
    }

    private static ImageResource[] LoadImages()
    {
        string assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
        return new[]
        {
            new ImageResource("img-compare", "image/png",
                System.IO.File.ReadAllBytes(System.IO.Path.Combine(assets, "test-img-gradient.png"))),
        };
    }
}
