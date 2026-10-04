using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using LumiText.Core.Documents;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;
using Windows.UI;

namespace LumiText.WinUI.Controls;

/// <summary>
/// 只读文档宿主控件（§9.1）：组合 <see cref="VirtualizedTextSurface"/>（§7.3 虚拟化）
/// 与 <see cref="FlowDocumentRenderer"/>（§6 块级渲染 + §8 图片）。
/// 不响应任何编辑输入；指针只用于滚动。浮动拖动不在这里（Phase 3 交互）。
/// </summary>
/// <remarks>
/// v4 修订：WinUI 3 的 <see cref="ScrollViewer"/> 是 <see langword="sealed"/>（设计 §9.1 的
/// 继承写法在 WASDK 不成立）——改为组合：本控件 = Grid 外壳 + 内部 ScrollViewer。
/// </remarks>
public sealed class LumiDocumentView : Grid
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);

    private readonly ScrollViewer _scroller;
    private readonly Grid _contentGrid;       // 撑开滚动范围（高 = 文档总高）
    private readonly Grid _surfaceHost;       // 可视宿主（位于内容 y=0）
    private readonly VirtualizedTextSurface _surface = new();
    private readonly FlowDocumentRenderer _renderer = new(new Win2DTextMeasurer());
    private readonly DocumentImageStore _imageStore = new(CanvasDevice.GetSharedDevice());
    private Document? _document;

    public LumiDocumentView()
    {
        _scroller = new ScrollViewer
        {
            // 命中测试铁律（S2 RESULTS §4.1）：背景必须设全透明画刷，不能留 null。
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _contentGrid = new Grid();
        _surfaceHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _contentGrid.Children.Add(_surfaceHost);
        _scroller.Content = _contentGrid;
        Children.Add(_scroller);

        _surface.RenderViewport = OnRenderViewport;
        _renderer.Images = _imageStore;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        // 内容区宽会变（滚动条 Auto 出现/消失、窗口缩放）：正文宽度按内容区算（见 ContentWidth）
        _contentGrid.SizeChanged += (_, _) => Relayout();
        _scroller.ViewChanged += OnViewChanged;
        Unloaded += (_, _) =>
        {
            _surface.Dispose();
            _imageStore.Dispose();
        };
    }

    /// <summary>排版耗时上报（沿用 FloatWrapView 的口径）。</summary>
    public event Action<double>? LayoutStatsChanged;

    /// <summary>行盒/带/段调试框线。</summary>
    public bool DebugOverlay { get; set; }

    /// <summary>最近一次可见区重绘耗时（毫秒，§10.3 数据源）。</summary>
    public double LastRedrawMs => _surface.LastRedrawMs;

    /// <summary>当前滚动偏移（DIP；探针/验收用）。</summary>
    public double VerticalOffset => _scroller.VerticalOffset;

    /// <summary>可滚动高度（DIP；探针/验收用）。</summary>
    public double ScrollableHeight => _scroller.ScrollableHeight;

    /// <summary>滚动到指定偏移（探针/验收用）。</summary>
    public bool ChangeView(double? horizontalOffset, double? verticalOffset, float? zoomFactor, bool disableAnimation) =>
        _scroller.ChangeView(horizontalOffset, verticalOffset, zoomFactor, disableAnimation);

    /// <summary>强制整面重绘当前表面区域（§10.3 重绘基准用；同步完成）。</summary>
    public void InvalidateSurface() => _surface.Invalidate();

    /// <summary>载入文档：排版上屏；图片后台解码，就绪后补画（§8.1）。</summary>
    public void SetDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        Relayout();
        _ = WarmupImagesAsync(document);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _surface.Attach(_surfaceHost);
        ApplyScrollBarChrome();
        Relayout();
    }

    /// <summary>滚动条：全程序通用的滑出/收起行为 + 箭头光标（只读宿主同样不该顶着 I 形光标）。</summary>
    private void ApplyScrollBarChrome()
    {
        _scroller.ApplyTemplate(); // 同 LumiEditor：Loaded 早于首次布局时滚动条可能还没现形
        ScrollBarReveal.AttachTo(_scroller);

        foreach (ScrollBar bar in ScrollBarReveal.FindAll<ScrollBar>(_scroller))
        {
            CursorShapes.SetShape(bar, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    private async Task WarmupImagesAsync(Document document)
    {
        await _imageStore.WarmupAsync(
            document.Images,
            () => DispatcherQueue.TryEnqueue(() => _surface.Invalidate()));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _surfaceHost.Height = e.NewSize.Height;
        Relayout();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        _surface.OnScroll((float)_scroller.VerticalOffset);

    private void Relayout()
    {
        if (_document is null || ActualWidth < 50 || ActualHeight < 50)
        {
            return;
        }
        var result = _renderer.UpdateLayout(_document, ContentWidth);
        _contentGrid.Height = Math.Max(result.TotalHeight, ActualHeight);
        _surfaceHost.Height = _contentGrid.Height;
        _surface.SetMetrics(ContentWidth, (float)ActualHeight, result.TotalHeight);
        LayoutStatsChanged?.Invoke(_renderer.LastLayoutDuration.TotalMilliseconds);
    }

    /// <summary>
    /// 正文排版宽度（DIP）：滚动内容区宽（已扣掉滚动条那一列）——按控件整宽排的话，
    /// 滚动条一露面就压住右边那几列字。首帧量不出来时退到内容网格宽、控件宽。
    /// </summary>
    private float ContentWidth
    {
        get
        {
            double viewport = _scroller.ViewportWidth;
            if (!(viewport > 1))
            {
                viewport = _contentGrid.ActualWidth;
            }
            if (!(viewport > 1))
            {
                viewport = ActualWidth;
            }
            return (float)Math.Max(1.0, viewport);
        }
    }

    private void OnRenderViewport(CanvasDrawingSession session, Windows.Foundation.Rect viewport, float scale)
    {
        if (_renderer.Current is null)
        {
            return;
        }
        _renderer.Render(session, _renderer.Current, viewport, InkColor, DebugOverlay);
    }
}
