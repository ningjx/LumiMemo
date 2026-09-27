using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
            window.Activated -= OnActivationChanged;
            window.Deactivated -= OnActivationChanged;
            window.Closed -= OnWindowClosed;
            window.Activated += OnActivationChanged;
            window.Deactivated += OnActivationChanged;
            window.Closed += OnWindowClosed;

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
            window.Activated -= OnActivationChanged;
            window.Deactivated -= OnActivationChanged;
            window.Closed -= OnWindowClosed;
            if (PresentationSource.FromVisual(window) is HwndSource)
            {
                Remove(window);
            }
        }
    }

    private static void OnActivationChanged(object? sender, EventArgs e)
    {
        var window = (Window)sender!;

        // DWM 会在激活状态切换完成时重新评估系统背景材质。若只在
        // SourceInitialized 设置一次，某些系统版本会把失焦窗口退回实色后不再恢复。
        // 排到本轮激活消息之后重申属性，避免与 DWM 自己的处理顺序互相覆盖。
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (GetEnabled(window) && window.IsVisible)
                {
                    Apply(window);
                }
            }));
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.Activated -= OnActivationChanged;
        window.Deactivated -= OnActivationChanged;
        window.Closed -= OnWindowClosed;
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

        bool applied = window.IsActive
            ? WindowInterop.TryEnableAcrylic(hwnd)
            : WindowInterop.TryEnableInactiveAcrylic(hwnd);

        if (!applied
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
