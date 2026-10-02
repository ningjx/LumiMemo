using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using LumiText.WinUI.Rendering;
using Windows.UI;

namespace LumiText.Demo;

/// <summary>
/// M1-U1 探针：验证 Phase 1 设计 §7.2 的「钉视口」滚动模型——
/// <c>GetScrollViewerManipulationPropertySet</c> 在本项目 WASDK 2.5.1 上的运行时行为，
/// 以及表达式动画 <c>Offset.Y = -Translation.Y</c> 把承载 surface 的 SpriteVisual
/// 钉回视口原位的符号正确性（文档覆盖到 2.0，2.5.1 属未验证新版本）。
/// </summary>
/// <remarks>
/// 判定方式：窗口加载 1.8s 后自动滚动到 1600dip；对比滚动前后截图——
/// 钉住成功 = PINNED surface 在视口原位不动，下方 XAML 编号行随滚动上移。
/// </remarks>
public sealed partial class ScrollPinWindow : Window
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);
    private static readonly Color PinFrameColor = Color.FromArgb(255, 180, 40, 60);

    private readonly DesktopAcrylicController? _backdrop;
    private readonly CompositionTextSurface _surface = new();
    private readonly DispatcherQueueTimer _autoScrollTimer;

    // 官方注意点：PropertySet 必须存为字段防 GC（表达式动画不持有强引用）。
    private CompositionPropertySet? _scrollProps;

    public ScrollPinWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 560));

        for (int i = 1; i <= 120; i++)
        {
            LinesPanel.Children.Add(new TextBlock
            {
                Text = $"XAML 滚动行 {i:000} —— 此行应随滚动上移离开视口",
                FontSize = 14,
                Foreground = new SolidColorBrush(InkColor),
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        _autoScrollTimer = DispatcherQueue.CreateTimer();
        _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(1800);
        _autoScrollTimer.IsRepeating = false;
        _autoScrollTimer.Tick += (_, _) =>
            Scroller.ChangeView(null, 1600, null, disableAnimation: true);

        Activated += OnFirstActivated;
        Closed += (_, _) =>
        {
            _autoScrollTimer.Stop();
            _surface.Dispose();
            _backdrop?.Dispose();
        };
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _surface.RenderContent = OnRenderContent;
        _surface.Attach(PinHost);

        // U1 核心：表达式动画钉视口。符号假设：滚动 S 后内容（含 PinHost）被合成器
        // 平移 -S（Translation.Y = -S），Sprite 的 Offset.Y = -Translation.Y = +S 恰好抵消。
        _scrollProps = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Scroller);
        var compositor = ElementCompositionPreview.GetElementVisual(PinHost).Compositor;
        var expression = compositor.CreateExpressionAnimation("-ScrollManipulation.Translation.Y");
        expression.SetReferenceParameter("ScrollManipulation", _scrollProps);
        _surface.Sprite?.StartAnimation("Offset.Y", expression);

        _autoScrollTimer.Start();
    }

    private void OnRenderContent(CanvasDrawingSession session, Vector2 sizeDips, float scale)
    {
        session.Transform = Matrix3x2.CreateScale(scale);

        var bounds = new Windows.Foundation.Rect(0.5, 0.5, sizeDips.X - 1, sizeDips.Y - 1);
        session.DrawRectangle(bounds, PinFrameColor, 2f);

        using var titleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };
        using var bodyFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 14,
        };
        session.DrawText("PINNED SURFACE", new Windows.Foundation.Rect(16, 14, sizeDips.X - 32, 44),
            PinFrameColor, titleFormat);
        session.DrawText(
            "这块自绘表面应钉在视口原位：滚动后下方 XAML 编号行上移离开，而本框与本行文字不动。\n" +
            "Pinned surface — must stay fixed in the viewport while XAML lines scroll away.",
            new Windows.Foundation.Rect(16, 62, sizeDips.X - 32, 90),
            InkColor, bodyFormat);

        // 标尺：便于像素级对比两张截图中 surface 是否位移。
        for (int y = 160; y < sizeDips.Y - 8; y += 40)
        {
            session.DrawLine(16, y, 16 + (y % 80 == 0 ? 48 : 24), y, PinFrameColor, 1.5f);
        }
    }
}
