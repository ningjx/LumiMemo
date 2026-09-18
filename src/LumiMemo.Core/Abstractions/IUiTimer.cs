namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 一个「安静满一段时间后回调一次」的 UI 线程定时器（§8.5、§11.1）。
/// </summary>
/// <remarks>
/// <para>
/// 抽这一层是为了<strong>可测</strong>：生产实现（<c>DispatcherTimerFactory</c>）必须跑在
/// 有消息泵的 UI 线程上，无头测试进程里没有那个东西。换成可手动推进的替身之后，
/// 「连续标记 5 次只落盘 1 次」这类节流逻辑就不必真的等满 5 秒，而且是确定性的（§21.5）。
/// </para>
/// <para>
/// <strong>一次性</strong>：每次 <see cref="Start"/> 之后最多回调一次，回调之后自动停止。
/// 去抖要的是「安静满 N 秒再动手」，周期性触发会让一次拖动之后的每一秒都往磁盘上写一遍。
/// </para>
/// <para>
/// 重复调 <see cref="Start"/> 表示<strong>重新计时</strong>，这正是去抖的合并语义：
/// 拖动窗口期间几十次 <c>WM_MOVE</c> 每次都把到期时刻往后推，松手满 1 秒后才真正写盘（§8.5）。
/// </para>
/// </remarks>
public interface IUiTimer : IDisposable
{
    /// <summary>（重新）开始计时，到期时在 UI 线程上调用 <paramref name="onTick"/> 一次。</summary>
    /// <param name="interval">从<strong>本次调用</strong>算起的延时。</param>
    /// <param name="onTick">
    /// 到期回调。做成参数而不是构造参数，是为了让工厂保持无状态、<c>Create()</c> 不带参，
    /// 同一个工厂因此能造出服务于不同用途的定时器。
    /// </param>
    void Start(TimeSpan interval, Action onTick);

    /// <summary>取消尚未到期的计时。已经到期或从未启动时什么也不做。</summary>
    void Stop();
}
