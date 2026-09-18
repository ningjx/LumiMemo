namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 时间来源（§21.5）。
/// </summary>
/// <remarks>
/// <strong>这个抽象是必须的</strong>：自动保存去抖、文件监听的抑制窗口、回收站保留期判断
/// 全部依赖时间。用真实时间的测试既慢又不稳定（flaky），有了本接口才能注入可手动推进的
/// <c>FakeClock</c>，让「500ms 后自动保存」这类测试变成确定性的（§21.5）。
/// 因此<strong>不要在业务代码里直接用 <c>DateTimeOffset.Now</c></strong>。
/// </remarks>
public interface IClock
{
    /// <summary>当前本地时间，带时区偏移。用于文件名日期、Front Matter 时间戳、保留期判断。</summary>
    DateTimeOffset Now { get; }

    /// <summary>当前 UTC 时间。只在确实需要统一时区基准时使用。</summary>
    DateTimeOffset UtcNow { get; }
}
