using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace LumiText.WinUI.Rendering;

/// <summary>
/// 渲染基座：一块经 Win2D 绘制的 <see cref="CompositionDrawingSurface"/>（预乘 Alpha、透明清屏），
/// 通过 SpriteVisual 挂进合成树。全程不引入 external content，
/// 因此可与 DesktopAcrylic 系统背景共存（S1 已验证 H1/H2/H3）。
/// </summary>
/// <remarks>
/// <para>渲染内容完全由 <see cref="RenderContent"/> 回调决定；本类只负责
/// surface/brush/visual 生命周期、尺寸同步与 DPI 缩放。</para>
/// <para>surface 按物理像素创建（DIP × RasterizationScale），
/// SurfaceBrush 以 Fill 映射回 DIP 尺寸，屏幕上净 1:1 —— 高 DPI 不模糊。</para>
/// </remarks>
public sealed class CompositionTextSurface : IDisposable
{
    private FrameworkElement? _host;
    private Compositor? _compositor;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private SpriteVisual? _sprite;
    private float _scale = 1f;
    private bool _disposed;

    /// <summary>
    /// 内容回调：参数为 (绘制会话, 可视尺寸 DIP, 光栅化倍率)。
    /// 会话已按透明清屏并设好灰阶抗锯齿；回调内的坐标系为 DIP（物理像素 = DIP × 倍率）。
    /// </summary>
    public Action<CanvasDrawingSession, Vector2, float>? RenderContent { get; set; }

    /// <summary>
    /// 承载 surface 的 SpriteVisual（只读）：表达式动画的挂载点，
    /// 例如滚动钉视口（Phase 1 §7.2，M1-U1 探针验证）。Attach 前为 null。
    /// </summary>
    public SpriteVisual? Sprite => _sprite;

    public void Attach(FrameworkElement host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null)
        {
            throw new InvalidOperationException("CompositionTextSurface 只能 Attach 一次。");
        }

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
                if (host.XamlRoot is not null)
                {
                    _scale = (float)host.XamlRoot.RasterizationScale;
                }
                RebuildSurface();
            };
        }
        RebuildSurface();
    }

    /// <summary>标记内容失效，按当前尺寸重绘（surface 未变化时不重建）。</summary>
    public void Invalidate()
    {
        if (_surface is null)
        {
            RebuildSurface();
            return;
        }
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

        using var session = CanvasComposition.CreateDrawingSession(_surface);
        // 透明清屏：内容间隙透出窗口背景（毛玻璃）。
        session.Clear(Color.FromArgb(0, 0, 0, 0));
        // 透明载体禁用 ClearType（子像素混合依赖不透明底会出彩边），灰阶 AA 是唯一正确选择。
        session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        RenderContent?.Invoke(session, _host.ActualSize, _scale);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
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
