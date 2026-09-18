using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;

namespace LumiMemo.Integration.Tests.TestDoubles;

/// <summary>
/// 一份写死的显示器清单（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 真实现（<c>MonitorEnumerator</c>）要调 Win32 枚举显示器，在测试里既跑不稳也不该跑。
/// 命中判定直接转交给 <see cref="LayoutMath.FindDisplayContaining"/>：
/// 那条规则在 Core 里只有一处实现，替身再抄一份就等于给自己制造了一个
/// 「替身和生产代码行为不一致」的隐患，而那种不一致只会让测试变成假的。
/// </remarks>
public sealed class RecordingDisplayProvider : IDisplayProvider
{
    private readonly List<DisplaySnapshot> _displays;

    public RecordingDisplayProvider(params DisplaySnapshot[] displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        _displays = [.. displays];

        if (_displays.Count > 0 && !_displays.Exists(display => display.IsPrimary))
        {
            // 一台主显示器都没有的清单在真实机器上不可能出现，
            // 让它在构造时就炸，好过让被测代码去处理一个不存在的场景。
            throw new ArgumentException("显示器清单里必须有一台主显示器。", nameof(displays));
        }
    }

    /// <summary>最常见的场景：一台 100% 的主屏 + 一台 150% 的副屏。</summary>
    public static RecordingDisplayProvider PrimaryAndSecondary() => new(
        new DisplaySnapshot(@"\\.\DISPLAY1", "主显示器", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96)
        {
            IsPrimary = true,
        },
        new DisplaySnapshot(@"\\.\DISPLAY2", "DELL U2720Q", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 144));

    /// <summary>清单里的全部显示器。</summary>
    public IReadOnlyList<DisplaySnapshot> All => _displays;

    /// <summary>主显示器。</summary>
    public DisplaySnapshot Primary =>
        _displays.Find(display => display.IsPrimary) ?? _displays[0];

    /// <summary>命中指定物理像素点的显示器。</summary>
    public DisplaySnapshot? FindDisplayContaining(double xPx, double yPx) =>
        LayoutMath.FindDisplayContaining(_displays, xPx, yPx);
}
