using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LumiMemo.WinUI.Controls;

/// <summary>图片缩放的提交请求（目标显示尺寸，px）。</summary>
internal sealed record ImageResizeRequest(int Index, int Width, int Height);

/// <summary>图片缩放手柄覆盖层：悬停提示框、八手柄、拖拽预览与提交。</summary>
/// <remarks>
/// <para>
/// 挂在编辑器上方的独立 Canvas 上。Canvas 不设 Background——空白区域不参与命中，指针事件
/// 穿透回编辑器，只有手柄元素吃事件。RichEditBox 自带"能拖、但没有任何手柄与光标提示"的
/// 图片缩放，这一层负责把交互显式化；它是独立覆盖层，和自带行为互不干扰。
/// </para>
/// <para>
/// 只记图片的起始字符索引，每次重定位都重新实测（不缓存尺寸）：滚动、撤销、换行都能自然
/// 跟上；该索引处不再是图片就自动隐藏（文本在图片前增删会让索引漂移，重点击即可）。
/// </para>
/// </remarks>
internal sealed class ImageAdorner
{
    private const double HandleSize = 11;
    private const double DropIndicatorHeight = 18;

    // 与 RichEditorHost 的编辑区 Padding 保持一致：候选口径「原点在文本区内」要用它换算。
    private const double EditorPaddingX = 18;
    private const double EditorPaddingY = 16;

    private static readonly Color OutlineColor = Color.FromArgb(0xCC, 0x7A, 0x6B, 0xB5);
    private static readonly Color HandleFill = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color HandleBorderColor = Color.FromArgb(0xCC, 0x5B, 0x4F, 0xA8);
    private static readonly Color LabelBackgroundColor = Color.FromArgb(0xCC, 0x33, 0x30, 0x3B);

    private readonly RichEditBox _editor;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _outline;
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeLabelText;
    private readonly Rectangle _dropIndicator;
    private readonly Dictionary<ResizeHandle, Border> _handles = new();

    private int _index = -1;
    private bool _selected;
    private RectD _rect;

    private ResizeHandle _dragHandle = ResizeHandle.None;
    private RectD _dragStartRect;
    private double _dragMaxWidth;

    public ImageAdorner(Grid host, RichEditBox editor)
    {
        _editor = editor;

        _outline = new Rectangle
        {
            Stroke = new SolidColorBrush(OutlineColor),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_outline);

        foreach (ResizeHandle handle in AdornerGeometry.AllHandles)
        {
            var box = new Border
            {
                Width = HandleSize,
                Height = HandleSize,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(HandleFill),
                BorderBrush = new SolidColorBrush(HandleBorderColor),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
                Tag = handle,
            };
            box.PointerPressed += OnHandlePressed;
            box.PointerMoved += OnHandleMoved;
            box.PointerReleased += OnHandleReleased;
            box.PointerCaptureLost += OnHandleCaptureLost;
            CursorShapes.SetShape(box, CursorFor(handle));
            _handles[handle] = box;
            _canvas.Children.Add(box);
        }

        _sizeLabelText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        };
        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(LabelBackgroundColor),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _sizeLabelText,
        };
        _canvas.Children.Add(_sizeLabel);

        // 拖放图片时的插入指示线（与手柄层共用这个覆盖层）。
        _dropIndicator = new Rectangle
        {
            Width = 2,
            Height = DropIndicatorHeight,
            Fill = new SolidColorBrush(OutlineColor),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_dropIndicator);

        host.Children.Add(_canvas);
    }

    /// <summary>拖拽结束、需要宿主落实新尺寸（宿主失败时会再调 <see cref="Refresh"/> 复原）。</summary>
    public event EventHandler<ImageResizeRequest>? ResizeCommitted;

    /// <summary>是否处于手柄模式（区别于悬停提示）。</summary>
    public bool IsSelected => _selected;

    /// <summary>当前是否有装饰在显示。</summary>
    public bool IsShowing => _index >= 0;

    /// <summary>选中某张图片：显示虚线框 + 八手柄。</summary>
    public void ShowForImage(int index)
    {
        if (!Measure(index, out RectD rect))
        {
            Hide();
            return;
        }

        _index = index;
        _selected = true;
        _rect = rect;
        ApplyVisuals();
    }

    /// <summary>悬停提示：宿主已用引擎自己的命中映射确认指针在图片上，这里只负责画框。</summary>
    public void HoverAt(int index)
    {
        if (_selected)
        {
            return;
        }

        if (!Measure(index, out RectD rect))
        {
            Hide();
            return;
        }

        _index = index;
        _selected = false;
        _rect = rect;
        ApplyVisuals();
    }

    /// <summary>收起悬停提示（手柄模式不受影响）。</summary>
    public void HideHover()
    {
        if (!_selected)
        {
            Hide();
        }
    }

    /// <summary>全部隐藏。</summary>
    public void Hide()
    {
        _index = -1;
        _selected = false;
        _dragHandle = ResizeHandle.None;
        _outline.Visibility = Visibility.Collapsed;
        _sizeLabel.Visibility = Visibility.Collapsed;
        foreach (Border box in _handles.Values)
        {
            box.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>重测当前图片（滚动、文本变化、窗口缩放后）。</summary>
    public void Refresh()
    {
        if (_index < 0)
        {
            return;
        }

        if (!Measure(_index, out RectD rect))
        {
            Hide();
            return;
        }

        _rect = rect;
        ApplyVisuals();
    }

    /// <summary>拖放插入指示线：放在某个字符位置的行首上方。</summary>
    public void ShowDropIndicator(int index)
    {
        if (!TryGetPoint(index, out PointD point))
        {
            HideDropIndicator();
            return;
        }

        Canvas.SetLeft(_dropIndicator, point.X - 1);
        Canvas.SetTop(_dropIndicator, point.Y - 2);
        _dropIndicator.Visibility = Visibility.Visible;
    }

    public void HideDropIndicator() => _dropIndicator.Visibility = Visibility.Collapsed;

    public void Dispose()
    {
        if (_canvas.Parent is Panel panel)
        {
            panel.Children.Remove(_canvas);
        }
    }

    /// <summary>
    /// 测图片的覆盖层矩形。坐标口径（原点在控件还是文本区、单位是逻辑还是物理像素）没有文档
    /// 保证，所以不做猜测：把候选换算后的点喂回<strong>引擎自己的命中映射</strong>
    /// （GetRangeFromPoint，与点击判定同一个 API）验证，谁的答案落在图片上就用谁——
    /// 任何机器、任何缩放下都成立，不需要写死校准值。尺寸优先用实测右下点（并用
    /// <c>\picwgoal</c> 交叉校验），退化时用推算尺寸；口径识别不出来的极端情况退回出界自检。
    /// </summary>
    private bool Measure(int index, out RectD rect)
    {
        rect = default;
        if (index < 0)
        {
            return false;
        }

        try
        {
            ITextRange range = _editor.Document.GetRange(index, index + 1);
            range.GetText(TextGetOptions.FormatRtf, out string fragment);
            if (!RtfPict.ContainsPict(fragment))
            {
                return false;
            }

            PointOptions options = PointOptions.Start | PointOptions.ClientCoordinates;
            range.GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, options, out Windows.Foundation.Point topLeft);
            range.GetPoint(HorizontalCharacterAlignment.Right, VerticalCharacterAlignment.Bottom, options, out Windows.Foundation.Point bottomRight);

            double rawWidth = bottomRight.X - topLeft.X;
            double rawHeight = bottomRight.Y - topLeft.Y;
            bool measured = rawWidth >= 8 && rawHeight >= 8
                && rawWidth <= 10000 && rawHeight <= 10000;

            double editorWidth = Math.Max(1, _editor.ActualWidth);
            double editorHeight = Math.Max(1, _editor.ActualHeight);
            double dpi = _editor.XamlRoot?.RasterizationScale ?? 1.0;

            AdornerGeometry.CoordinateTransform mapping = AdornerGeometry.DiscoverTransform(
                    new PointD(topLeft.X, topLeft.Y), dpi, EditorPaddingX, EditorPaddingY,
                    point => EngineSaysOnImage(point, index))
                ?? new AdornerGeometry.CoordinateTransform(
                    AdornerGeometry.ChooseScale(
                        new RectD(topLeft.X, topLeft.Y, Math.Max(rawWidth, 1), Math.Max(rawHeight, 1)),
                        editorWidth, editorHeight, dpi),
                    0, 0);

            double width;
            double height;
            if (measured)
            {
                width = rawWidth * mapping.Scale;
                height = rawHeight * mapping.Scale;

                // 右下点未必真是图片的外接角（可能是行框的角）；与 goal 推算值差太多就换推算尺寸。
                if (RtfPict.TryGetDisplaySize(fragment, out double goalWidth, out double goalHeight)
                    && goalWidth > 0
                    && (width / goalWidth < 0.5 || width / goalWidth > 2.0))
                {
                    width = goalWidth;
                    height = goalHeight;
                }
            }
            else if (RtfPict.TryGetDisplaySize(fragment, out width, out height))
            {
                // 实测不可用：尺寸用 goal 推算（本身就是逻辑像素口径，不再跟坐标缩放走）。
            }
            else
            {
                return false;
            }

            PointD origin = mapping.Apply(new PointD(topLeft.X, topLeft.Y));
            rect = new RectD(origin.X, origin.Y, width, height);

            // 完全滚出视野就不显示；部分露出保留。
            return rect.Bottom >= 0 && rect.Right >= 0 && rect.Y <= editorHeight && rect.X <= editorWidth;
        }
        catch (Exception)
        {
            // 文档正在变化（加载、撤销）时 GetPoint 可能失败，当作没测到。
            return false;
        }
    }

    /// <summary>问引擎：这一点落在图片字符上吗（和用户点击走同一套命中逻辑）。</summary>
    private bool EngineSaysOnImage(PointD point, int imageIndex)
    {
        try
        {
            int at = _editor.Document
                .GetRangeFromPoint(new Windows.Foundation.Point(point.X, point.Y), PointOptions.ClientCoordinates)
                .StartPosition;
            return at == imageIndex || at == imageIndex + 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryGetPoint(int index, out PointD point)
    {
        point = default;
        try
        {
            PointOptions options = PointOptions.Start | PointOptions.ClientCoordinates;
            _editor.Document.GetRange(index, index).GetPoint(
                HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, options, out Windows.Foundation.Point rawPoint);

            double dpi = _editor.XamlRoot?.RasterizationScale ?? 1.0;
            double scale = AdornerGeometry.ChooseScale(
                new RectD(rawPoint.X, rawPoint.Y, 1, 1),
                Math.Max(1, _editor.ActualWidth), Math.Max(1, _editor.ActualHeight), dpi);

            point = new PointD(rawPoint.X * scale, rawPoint.Y * scale);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ApplyVisuals()
    {
        UpdateRect(_rect);
        _outline.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Collapsed;
        foreach (Border box in _handles.Values)
        {
            box.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateRect(RectD rect)
    {
        Canvas.SetLeft(_outline, rect.X);
        Canvas.SetTop(_outline, rect.Y);
        _outline.Width = Math.Max(0, rect.Width);
        _outline.Height = Math.Max(0, rect.Height);

        foreach ((ResizeHandle handle, Border box) in _handles)
        {
            PointD center = AdornerGeometry.HandleCenter(rect, handle);
            Canvas.SetLeft(box, center.X - (HandleSize / 2));
            Canvas.SetTop(box, center.Y - (HandleSize / 2));
        }
    }

    private void OnHandlePressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Border box || box.Tag is not ResizeHandle handle)
        {
            return;
        }

        args.Handled = true;
        box.CapturePointer(args.Pointer);
        _dragHandle = handle;
        _dragStartRect = _rect;
        // 宽度上限：编辑区可视宽减掉编辑器左右内边距（18×2），保证不出现横向溢出。
        _dragMaxWidth = Math.Max(AdornerGeometry.MinEdge, _editor.ActualWidth - 36);
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragHandle == ResizeHandle.None)
        {
            return;
        }

        args.Handled = true;
        PointD pointer = ToPoint(args.GetCurrentPoint(_canvas).Position);
        PointD size = AdornerGeometry.Resize(_dragStartRect, _dragHandle, pointer, _dragMaxWidth);

        // 锚点 = 图片左上角：内嵌图片在行里就是从左上向右下展开，预览即结果。
        var preview = new RectD(_dragStartRect.X, _dragStartRect.Y, size.X, size.Y);
        UpdateRect(preview);
        ShowSizeLabel(preview);
    }

    private void OnHandleReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragHandle == ResizeHandle.None || sender is not Border box)
        {
            return;
        }

        args.Handled = true;
        ResizeHandle handle = _dragHandle;
        _dragHandle = ResizeHandle.None;
        box.ReleasePointerCapture(args.Pointer);

        PointD pointer = ToPoint(args.GetCurrentPoint(_canvas).Position);
        PointD size = AdornerGeometry.Resize(_dragStartRect, handle, pointer, _dragMaxWidth);

        _sizeLabel.Visibility = Visibility.Collapsed;
        ResizeCommitted?.Invoke(this, new ImageResizeRequest(_index, (int)Math.Round(size.X), (int)Math.Round(size.Y)));
    }

    private void OnHandleCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_dragHandle == ResizeHandle.None)
        {
            return;
        }

        // 拖拽被打断（释放捕获、窗口切走等）：取消这次预览。
        _dragHandle = ResizeHandle.None;
        _sizeLabel.Visibility = Visibility.Collapsed;
        UpdateRect(_rect);
    }

    private void ShowSizeLabel(RectD preview)
    {
        _sizeLabelText.Text = $"{preview.Width:0} × {preview.Height:0}";
        _sizeLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        double x = preview.X + preview.Width + 8;
        double y = preview.Y + preview.Height + 6;
        if (x + _sizeLabel.DesiredSize.Width > _canvas.ActualWidth)
        {
            x = Math.Max(0, preview.Right - _sizeLabel.DesiredSize.Width - 4);
        }

        if (y + _sizeLabel.DesiredSize.Height > _canvas.ActualHeight)
        {
            y = Math.Max(0, preview.Y - _sizeLabel.DesiredSize.Height - 4);
        }

        Canvas.SetLeft(_sizeLabel, x);
        Canvas.SetTop(_sizeLabel, y);
        _sizeLabel.Visibility = Visibility.Visible;
    }

    private static PointD ToPoint(Windows.Foundation.Point point) => new(point.X, point.Y);

    private static InputSystemCursorShape CursorFor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeHandle.Left or ResizeHandle.Right => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.SizeNorthSouth,
    };
}
