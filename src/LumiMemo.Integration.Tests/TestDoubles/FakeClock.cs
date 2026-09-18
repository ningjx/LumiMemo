using LumiMemo.Core.Abstractions;

namespace LumiMemo.Integration.Tests.TestDoubles;

/// <summary>
/// 可推进的假时钟（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 与 <c>LumiMemo.Core.Tests.TestDoubles.FakeClock</c> 是同一份东西。
/// 之所以各留一份而不是让测试工程互相引用：测试工程之间建立引用会让
/// 「跑 Core 的测试」变成一件会顺带编译并运行别的东西的事，得不偿失；
/// 这个替身本身只有二十行，复制一份比制造耦合便宜。
/// </remarks>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    /// <param name="start">起始时刻。缺省时用一个固定值，保证测试可重现。</param>
    public FakeClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public DateTimeOffset Now => _now;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _now.ToUniversalTime();

    /// <summary>把时钟向前推进一段时间。</summary>
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    /// <summary>把时钟拨到指定时刻。</summary>
    public void Set(DateTimeOffset value) => _now = value;
}
