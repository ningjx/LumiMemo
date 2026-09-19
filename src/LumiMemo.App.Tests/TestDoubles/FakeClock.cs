using LumiMemo.Core.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 可手动拨动的假时钟。
/// </summary>
/// <remarks>
/// <para>
/// 管理器的搜索要把「当前时刻」交给 §12.2 的「七天内 +30」那一档。用真实时钟的话，
/// 用例就得自己算出「三分钟前」这种相对量，跑得久了还会跨过日期边界。
/// 拨到某个固定时刻，断言才写得死。
/// </para>
/// <para>
/// 起始值与 <c>LumiMemo.Core.Tests</c> 里那份保持一致：将来若把某些用例在两个工程间搬，
/// 不必重算期望值。
/// </para>
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
