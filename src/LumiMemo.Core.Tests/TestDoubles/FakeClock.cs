using LumiMemo.Core.Abstractions;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 可推进的假时钟（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 「500ms 去抖后自动保存」这类测试如果用真实时钟，就只能靠 <c>Thread.Sleep</c> 等待，
/// 既慢又不稳定。把时间做成可以手动推进的量，测试才是确定性的（§21.5）。
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
