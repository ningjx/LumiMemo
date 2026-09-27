using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LumiMemo.Infrastructure.Windows;

namespace LumiMemo.App.Views;

/// <summary>在窗口句柄创建后启用系统毛玻璃，失败时保留 XAML 中的实色背景。</summary>
public static class WindowBackdrop
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(WindowBackdrop),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty FallbackBrushKeyProperty = DependencyProperty.RegisterAttached(
        "FallbackBrushKey",
        typeof(string),
        typeof(WindowBackdrop),
        new PropertyMetadata("LumiWindowSurfaceBrush"));

    public static string GetFallbackBrushKey(DependencyObject element) =>
        (string)element.GetValue(FallbackBrushKeyProperty);

    public static void SetFallbackBrushKey(DependencyObject element, string value) =>
        element.SetValue(FallbackBrushKeyProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window)
        {
            return;
        }

        if (e.NewValue is true)
        {
            if (PresentationSource.FromVisual(window) is HwndSource)
            {
                Apply(window);
            }
            else
            {
                window.SourceInitialized += OnSourceInitialized;
            }
        }
        else
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource)
            {
                Remove(window);
            }
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;

        Apply(window);
    }

    private static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        WindowInterop.PreferRoundedCorners(hwnd);

        if (!WindowInterop.TryEnableAcrylic(hwnd)
            || HwndSource.FromHwnd(hwnd)?.CompositionTarget is not { } composition)
        {
            return;
        }

        composition.BackgroundColor = Colors.Transparent;
        window.SetCurrentValue(Control.BackgroundProperty, Brushes.Transparent);
    }

    private static void Remove(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        WindowInterop.DisableAcrylic(hwnd);
        window.SetResourceReference(Control.BackgroundProperty, GetFallbackBrushKey(window));
    }
}
