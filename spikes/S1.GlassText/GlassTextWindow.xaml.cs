using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace S1.GlassText;

/// <summary>
/// 毛玻璃窗口外壳。亚克力参数照抄主项目 AcrylicBackdrop
/// （IsInputActive=true 固定：失焦玻璃不降级，即 H2 的验证对象）。
/// </summary>
public sealed partial class GlassTextWindow : Window
{
    private readonly DesktopAcrylicController? _backdropController;
    private readonly CompositionTextSurface _textSurface = new();
    private bool _attached;

    public GlassTextWindow()
    {
        InitializeComponent();

        if (DesktopAcrylicController.IsSupported())
        {
            _backdropController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base,
                TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
                TintOpacity = 0.34f,
                LuminosityOpacity = 0.58f,
                FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248),
            };
            _backdropController.AddSystemBackdropTarget(
                this.As<ICompositionSupportsSystemBackdrop>());
            _backdropController.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Light,
            });
        }
        else
        {
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
        }

        Activated += OnActivatedOnce;
        Closed += (_, _) =>
        {
            _textSurface.Dispose();
            _backdropController?.Dispose();
        };
    }

    private void OnActivatedOnce(object sender, WindowActivatedEventArgs args)
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        Activated -= OnActivatedOnce;

        _textSurface.Attach(TextHost);
        _textSurface.SetContent(
            "LumiText S1 — 玻璃上的自绘文本",
            "这一段文字由 Win2D 直接绘制到 CompositionDrawingSurface（透明、预乘 Alpha），" +
            "文字之间的间隙应当透出窗口背后的毛玻璃，而不是白色或黑色色块。\n" +
            "中文混排 English mix 🎉✨ emoji 渲染测试。The quick brown fox jumps over the lazy dog. " +
            "敏捷的棕色狐狸跳过懒狗。反复观察：聚焦与失焦时玻璃观感应当一致（IsInputActive 固定为 true）。" +
            "1234567890 字号灰度过渡应当均匀，没有彩色边缘。\n" +
            "调整窗口大小、跨屏幕拖动、切换 DPI（100%/150%/200%）时文字应当始终锐利。");
    }
}
