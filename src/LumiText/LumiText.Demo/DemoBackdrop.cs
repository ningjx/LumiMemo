using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace LumiText.Demo;

/// <summary>
/// Demo 各窗口共用的毛玻璃外壳配置（与 DemoWindow 内联配置同源：
/// IsInputActive=true，失焦玻璃不降级；Tint 等参数与主程序 AcrylicBackdrop 一致）。
/// DemoWindow 是 S2 已验收窗口，保持内联不动；M1 的新窗口统一走这里。
/// </summary>
internal static class DemoBackdrop
{
    public static DesktopAcrylicController? Apply(Window window, Grid rootGrid)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            rootGrid.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
            return null;
        }

        var controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
            TintOpacity = 0.34f,
            LuminosityOpacity = 0.58f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248),
        };
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Light,
        });
        return controller;
    }
}
