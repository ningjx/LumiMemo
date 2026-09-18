using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 一份写死的显示器清单（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 真实现（<c>MonitorEnumerator</c>）要调 Win32 枚举显示器，测试里既跑不稳也不该跑。
/// 命中判定直接转交给 <see cref="LayoutMath.FindDisplayContaining"/>：
/// 那条规则在 Core 里只有一处实现，替身再抄一份就等于给自己埋了一个
/// 「替身与生产代码行为不一致」的隐患，而那种不一致只会让测试变成假的。
/// </para>
/// <para>
/// <c>LumiMemo.Integration.Tests</c> 里有一份行为相同的 <c>RecordingDisplayProvider</c>，
/// 两边<strong>刻意不共享</strong>：测试工程不互相引用，而为一个三十行的替身
/// 再建一个共享工程，代价比重复大得多。
/// </para>
/// </remarks>
public sealed class FixedDisplayProvider : IDisplayProvider
{
    private readonly List<DisplaySnapshot> _displays;

    public FixedDisplayProvider(params DisplaySnapshot[] displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        _displays = [.. displays];
    }

    /// <summary>一台 100% 的显示器，覆盖原点。</summary>
    public static FixedDisplayProvider Single() => new(Displays.Create());

    public IReadOnlyList<DisplaySnapshot> All => _displays;

    public DisplaySnapshot Primary =>
        _displays.Find(display => display.IsPrimary) ?? _displays[0];

    public DisplaySnapshot? FindDisplayContaining(double xPx, double yPx) =>
        LayoutMath.FindDisplayContaining(_displays, xPx, yPx);
}
