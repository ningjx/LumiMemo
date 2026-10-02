using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using LumiText.Core.Documents;
using LumiText.Core.Layout;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;
using Windows.Foundation;
using Windows.UI;

namespace LumiText.WinUI.Controls;

/// <summary>
/// S2 验证控件：一篇纯文本文档 + 可拖动浮动块的环绕排版演示。
/// 展示三件事：文字环绕浮动块、拖动时实时重排（"飞到另一边"）、
/// 悬停浮动块的自定义光标（Hand）——后者也是 Phase 3 待办框光标的先验。
/// </summary>
public sealed class FloatWrapView : Grid
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);

    private readonly Grid _surfaceHost;
    private readonly CompositionTextSurface _surface = new();
    private readonly Win2DTextMeasurer _measurer = new();
    private readonly FlowDocumentRenderer _renderer;

    private IReadOnlyList<ParagraphBlock> _paragraphs = Array.Empty<ParagraphBlock>();
    private readonly List<FloatObject> _floats = new();

    private int _dragFloatId = -1;
    private Point _dragOffset;

    public FloatWrapView()
    {
        _renderer = new FlowDocumentRenderer(_measurer);
        _surfaceHost = new Grid();
        Children.Add(_surfaceHost);

        _surface.RenderContent = OnRenderContent;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Relayout();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    /// <summary>最近重排耗时变化时触发（毫秒）。</summary>
    public event Action<double>? LayoutStatsChanged;

    /// <summary>是否绘制行盒调试框线。</summary>
    public bool DebugOverlay { get; set; }

    /// <summary>设置文档内容（段落 + 浮动块），立即重排。</summary>
    public void SetDocument(IEnumerable<ParagraphBlock> paragraphs, IEnumerable<FloatObject> floats)
    {
        _paragraphs = paragraphs.ToArray();
        _floats.Clear();
        _floats.AddRange(floats);
        Relayout();
    }

    /// <summary>当前浮动块的只读快照（文档坐标）。</summary>
    public IReadOnlyList<FloatObject> Floats => _floats;

    public void Relayout()
    {
        if (ActualWidth < 8 || _paragraphs.Count == 0)
        {
            return;
        }
        _renderer.UpdateLayout(_paragraphs, _floats.ToArray(), (float)ActualWidth);
        _surface.Invalidate();
        LayoutStatsChanged?.Invoke(_renderer.LastLayoutDuration.TotalMilliseconds);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _surface.Attach(_surfaceHost);
        Relayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _surface.Dispose();
    }

    private void OnRenderContent(CanvasDrawingSession session, Vector2 sizeDips, float scale)
    {
        if (_renderer.Current is null)
        {
            return;
        }
        // 布局在 DIP 空间完成；经变换放大到物理像素，D2D 按变换后尺寸光栅化文字，保持锐利。
        session.Transform = Matrix3x2.CreateScale(scale);
        _renderer.Render(session, _renderer.Current, InkColor, DebugOverlay);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        var hit = _renderer.Current?.FloatAt((float)position.X, (float)position.Y);
        if (hit is null)
        {
            return;
        }
        _dragFloatId = hit.Id;
        _dragOffset = new Point(position.X - hit.Rect.X, position.Y - hit.Rect.Y);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;

        if (_dragFloatId >= 0)
        {
            int index = _floats.FindIndex(f => f.Id == _dragFloatId);
            if (index >= 0)
            {
                var f = _floats[index];
                float x = Math.Max(0, (float)(position.X - _dragOffset.X));
                float y = Math.Max(0, (float)(position.Y - _dragOffset.Y));
                _floats[index] = f.MovedTo(x, y);
                Relayout();
            }
            e.Handled = true;
            return;
        }

        // 悬停自定义光标：浮动块上 Hand，其余 IBeam。
        var hover = _renderer.Current?.FloatAt((float)position.X, (float)position.Y);
        ProtectedCursor = hover is not null
            ? InputSystemCursor.Create(InputSystemCursorShape.Hand)
            : InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFloatId >= 0)
        {
            _dragFloatId = -1;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }
}
