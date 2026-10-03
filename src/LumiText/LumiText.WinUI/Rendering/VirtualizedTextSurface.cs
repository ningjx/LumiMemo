using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace LumiText.WinUI.Rendering;

/// <summary>
/// 虚拟化文本 surface（§7.3，<see cref="CompositionTextSurface"/> 的姊妹实现，不动旧类）：
/// <see cref="CompositionVirtualDrawingSurface"/> 承载「视口 ±1 屏预取」的文档切片。
/// </summary>
/// <remarks>
/// <para><b>滚动模型（v4 修订，替代 §7.2 钉视口方案的等效简化）</b>：表面按「文档坐标原点
/// <c>originY</c>」跟踪滚动——SpriteVisual 位于滚动内容内、<c>Offset.Y = originY</c>，
/// 随内容自然平移（视口落在表面覆盖区间内时 <b>零重绘、零表达式动画</b>）；
/// 视口越出区间才移动 origin 并重绘。与钉视口模型效果等价（表面内容始终对得上文档），
/// 但小幅滚动完全免工，且不需要 PropertySet 存活管理。U1 验证过的表达式钉视口
/// 仍作为视口尺寸恒定模型的备选记录（RESULTS-Phase1 §1.2）。</para>
/// <para><b>尺寸与退避</b>：surface 高 = min(文档总高, 3 × 视口高) 物理像素（当前屏 ±1 屏）；
/// 文档 ≤ 3 屏时整面覆盖（便签常态，只画一次）。v1 不做 Scroll()/ScrollWithClip 像素搬移与
/// Trim 增量回收：origin 移动时整面重绘（§7.3「首版不做像素搬移优化」的既定口径），
/// 显存已被 3 屏上限约束。</para>
/// <para>DPI 沿用既有方案：物理像素建 surface、<c>Stretch = Fill</c> 映射回 DIP；
/// 绘制回调在「文档坐标系」作画（origin 平移由会话 Transform 承担）。</para>
/// </remarks>
public sealed class VirtualizedTextSurface : IDisposable
{
    /// <summary>
    /// 绘制回调：参数为 (绘制会话, 需覆盖的文档区域 DIP, 光栅化倍率)。
    /// 会话已按透明清屏并设好灰阶抗锯齿；回调内的坐标系为<b>文档坐标 DIP</b>
    /// （origin 平移已含在会话 Transform 中：物理像素 = (文档 Y − originY) × 倍率）。
    /// </summary>
    public Action<CanvasDrawingSession, Rect, float>? RenderViewport { get; set; }

    private Compositor? _compositor;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionVirtualDrawingSurface? _surface;
    private SpriteVisual? _sprite;
    private FrameworkElement? _host;

    private float _widthDips;
    private float _viewportHeightDips;
    private float _documentHeightDips;
    private float _wantWidthDips;      // 最近一次请求的表面逻辑宽（钳制后，DIP）
    private float _wantHeightDips;     // 最近一次请求的表面逻辑高（钳制后，DIP）
    private int _surfacePixelW;        // 当前 surface 的纹理像素宽
    private int _surfacePixelH;        // 当前 surface 的纹理像素高
    private float _surfaceWidthDips;   // surface 逻辑宽 = 像素反推（pixel / scale）
    private float _surfaceHeightDips;  // surface 逻辑高 = 像素反推（pixel / scale）
    private float _scale = 1f;
    private float _originY;
    private bool _pendingRedraw;

    /// <summary>
    /// surface 单边像素上限。D3D11 纹理硬上限 16384，拉大窗口时 surface 像素尺寸
    /// （宽/高 DIP × scale）若超限，CreateDrawingSession 抛 ArgumentException
    /// （Win2D 内建限制，无 API 预查）——钳到安全值之下避免崩溃。
    /// </summary>
    private const float MaxSurfacePixelEdge = 16000f;

    /// <summary>最近一次整面重绘耗时（毫秒；§10.3 一屏重绘基准的数据源）。</summary>
    public double LastRedrawMs { get; private set; }

    /// <summary>当前表面覆盖的文档原点（DIP）。</summary>
    public float OriginY => _originY;

    /// <summary>把 surface 的可视宿主元素装进滚动内容（宿主须位于内容坐标 y=0）。</summary>
    public void Attach(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            _compositor, CanvasDevice.GetSharedDevice());

        var container = _compositor.CreateContainerVisual();
        _sprite = _compositor.CreateSpriteVisual();
        container.Children.InsertAtTop(_sprite);
        ElementCompositionPreview.SetElementChildVisual(host, container);
        if (host.XamlRoot is not null)
        {
            _scale = (float)host.XamlRoot.RasterizationScale;
            // DPI 缩放运行时变化（跨屏拖动、Loaded 时读到的还是未稳定值）：surface 按旧
            // _scale 建的物理像素被 Stretch=Fill 拉伸，就是发虚——与 CompositionTextSurface
            // 同一纪律：监听 Changed，更新 _scale 并整面重建。
            host.XamlRoot.Changed += OnXamlRootChanged;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        float newScale = (float)sender.RasterizationScale;
        if (Math.Abs(newScale - _scale) < 0.001f)
        {
            return;
        }
        _scale = newScale;
        // 用最近一次请求的度量整流程重跑（重算纹理上限钳制 + 重建 + 重绘）
        SetMetrics(_widthDips, _viewportHeightDips, _documentHeightDips);
    }

    /// <summary>设定/更新度量（内容宽、视口高、文档总高，DIP）并整面重绘。</summary>
    public void SetMetrics(float widthDips, float viewportHeightDips, float documentHeightDips)
    {
        _widthDips = Math.Max(0, widthDips);
        _viewportHeightDips = Math.Max(1, viewportHeightDips);
        _documentHeightDips = Math.Max(0, documentHeightDips);
        float surfaceHeight = Math.Min(_documentHeightDips, _viewportHeightDips * 3f);

        if (_compositor is null || _sprite is null || _graphicsDevice is null
            || _widthDips < 1 || surfaceHeight < 1)
        {
            return;
        }

        // 期望纹理像素尺寸（DIP × 倍率上取整）。
        // 重建判断比较「像素」而不是浮点 DIP：ceil 之后逻辑尺寸与请求值总有
        // <1px/scale 的差，按浮点比较会每次都判「需要重建」。
        float maxDipW = MaxSurfacePixelEdge / _scale;
        float maxDipH = MaxSurfacePixelEdge / _scale;
        _wantWidthDips = Math.Min(_widthDips, maxDipW);
        _wantHeightDips = Math.Min(surfaceHeight, maxDipH);
        int wantPixelW = Math.Max(1, (int)Math.Ceiling(_wantWidthDips * _scale));
        int wantPixelH = Math.Max(1, (int)Math.Ceiling(_wantHeightDips * _scale));

        if (_surface is null || wantPixelW != _surfacePixelW || wantPixelH != _surfacePixelH)
        {
            RebuildSurface();
        }
        Redraw(_originY);
    }

    /// <summary>滚动偏移（文档坐标 DIP）。视口在表面覆盖区间内 → 零开销；越出 → 移 origin 重绘。</summary>
    public void OnScroll(float scrollY)
    {
        if (_surface is null)
        {
            return;
        }
        float target = ComputeOrigin(scrollY);
        if (Math.Abs(target - _originY) > 0.5f)
        {
            Redraw(target);
        }
    }

    /// <summary>是否已 Attach（sprite 就绪）。未 Attach 时 Invalidate 是合法空操作。</summary>
    public bool IsAttached => _sprite is not null;

    /// <summary>强制整面重绘当前区域（重排/图片就绪/调试开关后调用）。</summary>
    public void Invalidate()
    {
        if (_surface is not null && _sprite is not null)
        {
            Redraw(_originY);
        }
    }

    public void Dispose()
    {
        if (_host?.XamlRoot is not null)
        {
            _host.XamlRoot.Changed -= OnXamlRootChanged;
        }
        _host = null;
        _surface?.Dispose();
        _graphicsDevice?.Dispose();
        _sprite?.Dispose();
    }

    /// <summary>
    /// 双阈值 origin 决策（值域钳制后的自然两态，§7.3 当前屏 ±1 屏预取）：
    /// 视口顶低于 origin + 0.5 屏 → 吸附「视口顶 − 1 屏」；
    /// 视口底高于 origin + 表面高 − 0.5 屏 → 吸附「视口底 − 2 屏」；
    /// 其余保持（含 origin = 0 与文档底的钳制端点——这两个位置的吸附结果与现状相等，
    /// 天然稳定，不会来回振荡）。
    /// </summary>
    private float ComputeOrigin(float scrollY)
    {
        float maxOrigin = Math.Max(0f, _documentHeightDips - _surfaceHeightDips);
        float viewBottom = scrollY + _viewportHeightDips;
        float target;
        if (scrollY < _originY + _viewportHeightDips * 0.5f)
        {
            target = scrollY - _viewportHeightDips;
        }
        else if (viewBottom > _originY + _surfaceHeightDips - _viewportHeightDips * 0.5f)
        {
            target = viewBottom - _viewportHeightDips * 2f;
        }
        else
        {
            return _originY;
        }
        return Math.Clamp(target, 0f, maxOrigin);
    }

    private void RebuildSurface()
    {
        _surface?.Dispose();
        // 像素尺寸由 SetMetrics 算好的「期望值」上取整（含纹理上限钳制）。
        int pixelW = Math.Max(1, (int)Math.Ceiling(_wantWidthDips * _scale));
        int pixelH = Math.Max(1, (int)Math.Ceiling(_wantHeightDips * _scale));
        _surface = _graphicsDevice!.CreateVirtualDrawingSurface(
            new Windows.Graphics.SizeInt32(pixelW, pixelH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        var brush = _compositor!.CreateSurfaceBrush(_surface);
        brush.Stretch = CompositionStretch.Fill;
        _sprite!.Brush = brush;
        // sprite 逻辑尺寸 = 像素反推 DIP（pixel / scale），不是请求的 DIP：
        // ceil 建纹理后 pixel ≥ want × scale，若 sprite 仍用 want，Fill 会把
        // pixelW 压回 want——整面纹理亚像素重采样，笔画发虚。反推后纹理→屏幕严格 1:1。
        _sprite.Size = new Vector2(pixelW / _scale, pixelH / _scale);
        _sprite.Offset = new Vector3(0, _originY, 0);
        // 记录实际像素与反推逻辑尺寸（Redraw 的 updateRect / 视口范围用，与纹理严格对应）
        _surfacePixelW = pixelW;
        _surfacePixelH = pixelH;
        _surfaceWidthDips = pixelW / _scale;
        _surfaceHeightDips = pixelH / _scale;
    }

    private void Redraw(float newOrigin)
    {
        if (_surface is null || _pendingRedraw)
        {
            return;
        }
        _pendingRedraw = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _originY = newOrigin;
            _sprite!.Offset = new Vector3(0, _originY, 0);

            // updateRect 直接用当前纹理像素尺寸（RebuildSurface 记录，不复算避免浮点误差）
            var updateRect = new Rect(0, 0, _surfacePixelW, _surfacePixelH);
            using (var session = CanvasComposition.CreateDrawingSession(_surface, updateRect))
            {
                // 与全部渲染路径同一纪律：透明清屏 + 灰阶 AA（M1-U3 已验证虚拟 surface 等价）。
                session.Clear(Color.FromArgb(0, 0, 0, 0));
                session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                // 回调在文档坐标作画：X 方向倍率，Y 方向倍率 + origin 平移。
                session.Transform = Matrix3x2.CreateScale(_scale)
                    * Matrix3x2.CreateTranslation(0, -_originY * _scale);
                RenderViewport?.Invoke(session,
                    new Rect(0, _originY, _surfaceWidthDips, _surfaceHeightDips), _scale);
            }
        }
        catch (ArgumentException)
        {
            // CreateDrawingSession 在极端尺寸下抛 ArgumentException（Win2D 内建纹理限制，
            // 无 API 预查；CommunityToolkit 同款处理）——跳过本次重绘，不让应用崩。
        }
        finally
        {
            watch.Stop();
            LastRedrawMs = watch.Elapsed.TotalMilliseconds;
            _pendingRedraw = false;
        }
    }
}
