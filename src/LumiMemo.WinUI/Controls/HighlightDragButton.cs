using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LumiMemo.WinUI.Controls;

internal sealed partial class HighlightDragButton : Button
{
    internal HighlightPicker? Picker { get; set; }
    internal bool SuppressClick { get; private set; }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PointerCaptures?.Any(pointer => pointer.PointerId == e.Pointer.PointerId) == true)
        {
            Picker?.TrackPress(e);
        }
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        Picker?.TrackMove(e);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        // Let Button reset its pressed state and release capture. Its Click handler
        // ignores the release if the press had already turned into a drag.
        SuppressClick = Picker?.FinishPress(e) == true;
        try
        {
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
        Picker?.CancelPress(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        Picker?.CancelPress(e);
    }
}
