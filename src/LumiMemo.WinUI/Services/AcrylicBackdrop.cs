using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace LumiMemo.WinUI.Services;

/// <summary>窗口的系统亚克力背景；系统不支持时给根面板铺实色兜底。</summary>
/// <remarks>
/// <para>
/// 便签窗口与管理器原本各抄了一份同样的初始化，这里合并成一处：tint 色与配置只有一个来源，
/// 后续做「整体毛玻璃」时改这一个文件就够。
/// </para>
/// <para>
/// <c>IsInputActive</c> 固定为 true：合成器因此始终按「活动窗口」渲染材质，窗口失焦玻璃不降级。
/// 这正是从 WPF 迁到 WinUI 的原因之一——WPF 走 DWM 属性路径，失焦退成实色且应用侧无解。
/// </para>
/// </remarks>
internal sealed class AcrylicBackdrop : IDisposable
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    private AcrylicBackdrop()
    {
    }

    /// <summary>
    /// 给窗口套上亚克力；系统不支持时返回 <see langword="null"/>，并给
    /// <paramref name="fallback"/> 铺上与材质同色的实色背景。
    /// </summary>
    public static AcrylicBackdrop? Apply(Window window, Panel fallback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fallback);

        if (!DesktopAcrylicController.IsSupported())
        {
            fallback.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
            return null;
        }

        var backdrop = new AcrylicBackdrop
        {
            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Light,
            },
        };

        backdrop._controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
            TintOpacity = 0.34f,
            LuminosityOpacity = 0.58f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248),
        };
        backdrop._controller.AddSystemBackdropTarget(
            window.As<ICompositionSupportsSystemBackdrop>());
        backdrop._controller.SetSystemBackdropConfiguration(backdrop._configuration);

        return backdrop;
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
    }
}
