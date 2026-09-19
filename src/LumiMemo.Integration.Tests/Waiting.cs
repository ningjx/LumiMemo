using System.Diagnostics;
using Xunit;

namespace LumiMemo.Integration.Tests;

/// <summary>
/// 轮询等一个条件成立，超时即失败（§21.3）。
/// </summary>
/// <remarks>
/// <para>
/// 文件系统事件在<strong>线程池线程</strong>上到达，而且 Windows 传达到底要多久没有上界
/// （杀毒软件、索引器、同步盘都会插一脚）。因此这类断言既不能用「写完立刻断言」，
/// 更不能用固定 <c>Sleep</c>：前者是间歇性失败的来源，后者要么慢得离谱、要么在网络盘上照样不够。
/// </para>
/// <para>
/// 轮询加超时的代价是「失败时要等满 <paramref name="timeout"/>」，换来的是一条
/// 又快又不会偶发飘红的用例。§21.3 明确要求这一点。
/// </para>
/// </remarks>
public static class Waiting
{
    /// <summary>两次轮询之间的间隔。25 毫秒：比事件本身通常的到达延迟大得多，又不至于空转。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>§21.3 定的默认上限。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等到 <paramref name="condition"/> 成立为止；超时则抛 <see cref="TimeoutException"/>。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        // 最后一次判定放在循环外：若刚好处在循环边界上成立了，不该白等满超时再报错。
        if (!condition())
        {
            throw new TimeoutException(
                $"等了 {timeout.TotalSeconds:0.#} 秒，条件仍未成立（轮询间隔 {PollInterval.TotalMilliseconds:0} 毫秒）。");
        }
    }

    /// <summary>用默认超时等到 <paramref name="condition"/> 成立。</summary>
    public static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(condition, DefaultTimeout);
}
