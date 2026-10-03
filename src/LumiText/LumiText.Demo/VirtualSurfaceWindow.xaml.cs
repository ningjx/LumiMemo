using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace LumiText.Demo;

/// <summary>
/// M1-U3 探针：验证 <see cref="CompositionVirtualDrawingSurface"/> 在「透明清屏 + 灰阶 AA +
/// DesktopAcrylic」场景与 S1 普通 surface 结论等价（文字间隙透玻璃、无黑底）——
/// Phase 1 设计 §7.1 指出虚拟 surface 是滚动虚拟化的关键，但 S1 只验过普通 surface。
/// </summary>
/// <remarks>
/// 判定方式：截图目视（文字间隙透出桌面模糊内容、非纯色/黑色块）；
/// 裁剪放大图随 RESULTS-Phase1 存档。
/// </remarks>
public sealed partial class VirtualSurfaceWindow : Window
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);

    private readonly DesktopAcrylicController? _backdrop;
    private Compositor? _compositor;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionVirtualDrawingSurface? _virtualSurface;
    private SpriteVisual? _sprite;
    private float _scale = 1f;

    public VirtualSurfaceWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 480));

        Activated += OnFirstActivated;
        Closed += (_, _) =>
        {
            _virtualSurface?.Dispose();
            _graphicsDevice?.Dispose();
            _sprite?.Dispose();
            _backdrop?.Dispose();
        };
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _compositor = ElementCompositionPreview.GetElementVisual(SurfaceHost).Compositor;
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            _compositor, CanvasDevice.GetSharedDevice());

        var container = _compositor.CreateContainerVisual();
        _sprite = _compositor.CreateSpriteVisual();
        container.Children.InsertAtTop(_sprite);
        ElementCompositionPreview.SetElementChildVisual(SurfaceHost, container);

        SurfaceHost.SizeChanged += (_, _) => RebuildAndDraw();
        if (SurfaceHost.XamlRoot is not null)
        {
            _scale = (float)SurfaceHost.XamlRoot.RasterizationScale;
        }
        RebuildAndDraw();
    }

    private void RebuildAndDraw()
    {
        if (_graphicsDevice is null || _sprite is null)
        {
            return;
        }
        var dips = SurfaceHost.ActualSize;
        if (dips.X < 1 || dips.Y < 1)
        {
            return;
        }

        int pixelW = Math.Max(1, (int)Math.Ceiling(dips.X * _scale));
        int pixelH = Math.Max(1, (int)Math.Ceiling(dips.Y * _scale));

        _virtualSurface?.Dispose();
        _virtualSurface = _graphicsDevice.CreateVirtualDrawingSurface(
            new Windows.Graphics.SizeInt32(pixelW, pixelH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        var brush = _compositor!.CreateSurfaceBrush(_virtualSurface);
        brush.Stretch = CompositionStretch.Fill;
        _sprite.Brush = brush;
        // sprite 逻辑尺寸 = 像素反推 DIP（与 CompositionTextSurface/VirtualizedTextSurface 同纪律：
        // ceil 建纹理后 Fill 回原始 DIP 会亚像素重采样，文字发虚）
        _sprite.Size = new System.Numerics.Vector2(pixelW / _scale, pixelH / _scale);

        // 虚拟 surface 必须按区域更新（V4：CreateDrawingSession(surface, updateRect)）。
        var updateRect = new Windows.Foundation.Rect(0, 0, pixelW, pixelH);
        using (var session = CanvasComposition.CreateDrawingSession(_virtualSurface, updateRect))
        {
            // 与普通 surface 完全相同的绘制纪律：透明清屏 + 灰阶 AA。
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            session.Transform = Matrix3x2.CreateScale(_scale);

            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 15,
            };
            using var bigFormat = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            };
            double w = dips.X - 32;
            session.DrawText("CompositionVirtualDrawingSurface 透明验证",
                new Windows.Foundation.Rect(16, 12, w, 40), InkColor, bigFormat);
            session.DrawText(
                "本段文字绘制在虚拟 surface 上：透明清屏 + 灰阶抗锯齿，与普通 surface 纪律相同。" +
                "文字间隙应透出 DesktopAcrylic 毛玻璃（非纯色块、非黑底）。" +
                "The quick brown fox jumps over the lazy dog. 中英混排与 emoji 🦊🍕🚀 的字体回退也应正常。" +
                "若本区域呈现黑色或不透明补丁，则 U3 判败，Phase 1 滚动虚拟化需改走普通 surface + 高度上限保护。",
                new Windows.Foundation.Rect(16, 60, w, dips.Y - 72), InkColor, format);
        }
    }
}
