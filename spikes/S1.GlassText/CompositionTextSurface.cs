using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace S1.GlassText;

/// <summary>
/// S1 核心对象：把文字经 Win2D 画到 <see cref="CompositionDrawingSurface"/>（预乘 Alpha、透明清屏），
/// 再用 SpriteVisual 挂进合成树。全程不引入 external content（无 SwapChainPanel/CanvasControl），
/// 用于验证 H1（文字间隙透出毛玻璃）/ H3（无彩边）/ H4（DPI 清晰）。
/// </summary>
internal sealed class CompositionTextSurface : IDisposable
{
    private FrameworkElement? _host;
    private Compositor? _compositor;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private SpriteVisual? _sprite;

    private string _title = "";
    private string _body = "";
    private float _scale = 1f;

    public void Attach(FrameworkElement host)
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            _compositor, CanvasDevice.GetSharedDevice());

        var container = _compositor.CreateContainerVisual();
        _sprite = _compositor.CreateSpriteVisual();
        container.Children.InsertAtTop(_sprite);
        ElementCompositionPreview.SetElementChildVisual(host, container);

        host.SizeChanged += (_, _) => RebuildSurface();
        if (host.XamlRoot is not null)
        {
            _scale = (float)host.XamlRoot.RasterizationScale;
            host.XamlRoot.Changed += (_, _) =>
            {
                _scale = (float)host.XamlRoot.RasterizationScale;
                RebuildSurface();
            };
        }
        RebuildSurface();
    }

    public void SetContent(string title, string body)
    {
        _title = title;
        _body = body;
        Render();
    }

    private void RebuildSurface()
    {
        if (_host is null || _compositor is null || _graphicsDevice is null || _sprite is null)
        {
            return;
        }

        var dips = _host.ActualSize;
        if (dips.X < 1 || dips.Y < 1)
        {
            return;
        }

        // 按物理像素建 surface，brush 用 Fill 映射回 DIP 尺寸 → 屏幕上净 1:1，200% DPI 不糊。
        int pixelW = Math.Max(1, (int)Math.Ceiling(dips.X * _scale));
        int pixelH = Math.Max(1, (int)Math.Ceiling(dips.Y * _scale));

        _surface?.Dispose();
        _surface = _graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pixelW, pixelH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        var brush = _compositor.CreateSurfaceBrush(_surface);
        brush.Stretch = CompositionStretch.Fill;
        _sprite.Brush = brush;
        _sprite.Size = dips;

        Render();
    }

    private void Render()
    {
        if (_surface is null || _host is null)
        {
            return;
        }

        var dips = _host.ActualSize;
        float s = _scale;
        float w = dips.X * s;
        float h = dips.Y * s;

        using var session = CanvasComposition.CreateDrawingSession(_surface);
        // 透明清屏：文字间隙必须露出背后的毛玻璃（H1）。
        session.Clear(Color.FromArgb(0, 0, 0, 0));
        // 透明载体上禁用 ClearType（子像素混合依赖不透明底，会出彩边），灰阶 AA 是唯一正确选择（H3）。
        session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;

        var ink = Color.FromArgb(255, 40, 32, 48);

        float y = 0;
        using (var titleFormat = new CanvasTextFormat { FontSize = 26f * s, WordWrapping = CanvasWordWrapping.Wrap })
        using (var titleLayout = new CanvasTextLayout(session, _title, titleFormat, w, h))
        {
            session.DrawTextLayout(titleLayout, 0, y, ink);
            y += (float)titleLayout.LayoutBounds.Height + 8f * s;
        }

        // 半透明装饰条：同时验证非文字像素的 Alpha 混合是否正常。
        session.FillRectangle(0, y, Math.Min(w, 180f * s), 3f * s, Color.FromArgb(100, 122, 90, 220));
        y += 3f * s + 8f * s;

        using var bodyFormat = new CanvasTextFormat { FontSize = 15f * s, WordWrapping = CanvasWordWrapping.Wrap };
        using var bodyLayout = new CanvasTextLayout(session, _body, bodyFormat, w, h - y);
        session.DrawTextLayout(bodyLayout, 0, y, ink);
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
        _sprite?.Dispose();
        _sprite = null;
        _host = null;
        _compositor = null;
    }
}
