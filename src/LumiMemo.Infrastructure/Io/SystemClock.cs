using LumiMemo.Core.Abstractions;

namespace LumiMemo.Infrastructure.Io;

/// <summary>
/// <see cref="IClock"/> 的生产实现，直接转发到系统的当前时间（§21.5）。
/// </summary>
/// <remarks>
/// 刻意做成一个只有两行的类：业务代码依赖 <see cref="IClock"/> 而不是
/// <c>DateTimeOffset.Now</c>，测试才能注入可推进的 <c>FakeClock</c>，
/// 让「500ms 后自动保存」这类测试变成确定性的（§21.5）。
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
