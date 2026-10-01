using LumiMemo.Core.Abstractions;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>可推进的假时钟（§21.5 的替身表）。</summary>
/// <remarks>
/// 与 Core.Tests / Integration.Tests 各持一份同样的东西，刻意不跨工程共享：
/// 测试工程之间建引用会让「跑本工程的测试」变成顺带编译并运行别的东西。
/// </remarks>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset Now => _now;

    public DateTimeOffset UtcNow => _now.ToUniversalTime();

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
