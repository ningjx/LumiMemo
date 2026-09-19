using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 一份写死的显示器清单。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LayoutService"/> 的构造函数要一个 <see cref="IDisplayProvider"/>，
/// 而本工程测的那些 ViewModel 从不真的去算窗口位置——它们只用 <see cref="LayoutService.All"/>。
/// 于是这里给一台覆盖原点的显示器，够构造起来就行；命中判定仍转交给
/// <see cref="LayoutMath.FindDisplayContaining"/>，不另抄一份规则。
/// </para>
/// <para>
/// <c>LumiMemo.Core.Tests</c> 里有一份功能更全的同名替身，两边刻意不共享（见
/// <see cref="InMemoryLayoutStore"/> 的说明）。
/// </para>
/// </remarks>
public sealed class FixedDisplayProvider : IDisplayProvider
{
    private readonly List<DisplaySnapshot> _displays;

    public FixedDisplayProvider(params DisplaySnapshot[] displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        _displays = displays.Length > 0 ? [.. displays] : [Single()];
    }

    /// <summary>一台 1920×1080、100%、覆盖原点的显示器。</summary>
    public static DisplaySnapshot Single() =>
        new(@"\\.\DISPLAY1", "测试显示器", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96)
        {
            IsPrimary = true,
        };

    public IReadOnlyList<DisplaySnapshot> All => _displays;

    public DisplaySnapshot Primary =>
        _displays.Find(static display => display.IsPrimary) ?? _displays[0];

    public DisplaySnapshot? FindDisplayContaining(double xPx, double yPx) =>
        LayoutMath.FindDisplayContaining(_displays, xPx, yPx);
}
