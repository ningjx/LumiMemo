using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LumiMemo.WinUI.Controls;

internal sealed partial class ToolbarDragButton : Button
{
    private const double DragThreshold = 4;

    private uint? _pointerId;
    private Point _pressPoint;
    private bool _dragStarted;

    internal event Action<double>? DragStarted;
    internal event Action<double>? DragMoved;
    internal event Action<double>? DragCompleted;
    internal event Action? DragCanceled;
    internal bool SuppressClick { get; private set; }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(XamlRoot.Content);
        if (_pointerId is null && point.PointerDeviceType == PointerDeviceType.Mouse &&
            point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed &&
            PointerCaptures?.Any(pointer => pointer.PointerId == e.Pointer.PointerId) == true)
        {
            _pointerId = e.Pointer.PointerId;
            _pressPoint = point.Position;
            _dragStarted = false;
        }
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(XamlRoot.Content);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (_dragStarted)
        {
            DragMoved?.Invoke(point.Position.X);
            return;
        }

        double dx = point.Position.X - _pressPoint.X;
        double dy = point.Position.Y - _pressPoint.Y;
        if (dx * dx + dy * dy >= DragThreshold * DragThreshold)
        {
            _dragStarted = true;
            DragStarted?.Invoke(point.Position.X);
        }
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        // Let Button reset its pressed state and capture. Ignore its Click if a drag started.
        SuppressClick = _pointerId == e.Pointer.PointerId && _dragStarted;
        try
        {
            if (_pointerId == e.Pointer.PointerId)
            {
                var point = e.GetCurrentPoint(XamlRoot.Content);
                if (!point.Properties.IsLeftButtonPressed)
                {
                    _pointerId = null;
                    if (_dragStarted)
                    {
                        DragCompleted?.Invoke(point.Position.X);
                    }
                }
            }
            base.OnPointerReleased(e);
        }
        finally
        {
            SuppressClick = false;
        }
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        CancelDrag(e);
    }

    private void CancelDrag(PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            _pointerId = null;
            if (_dragStarted)
            {
                DragCanceled?.Invoke();
            }
        }
    }
}
