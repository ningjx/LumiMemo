namespace LumiMemo.App.Abstractions;

/// <summary>
/// 「让程序退出」这条路，抽象出来使托盘菜单与关闭策略不直接碰 <see cref="System.Windows.Application"/>（§18.6）。
/// </summary>
/// <remarks>
/// <para>
/// 它存在的理由与 <see cref="IDialogService"/> 一样：让 ViewModel 与窗口策略依赖抽象，
/// 于是测试里能断言「点了退出」这件事确实发生了，而不必真的把测试进程关掉。
/// </para>
/// <para>
/// <strong>它只是发起退出，不做退出本身。</strong> §17.4 那一整套收尾
/// （停自动保存 → flush 未落盘的便签 → 写 layout.json）在 <c>StartupSequence</c> 里，
/// 由 <c>App.OnExit</c> 触发。这里若是自己写一套收尾，两条路径迟早会对不上。
/// </para>
/// <para>
/// <strong>没有「取消退出」这条路。</strong> §17.4 的 4b 分支（有便签没保存上，
/// 用户可以选择不退出）发生在收尾过程内部，由那一层自己设法中止——
/// 把「中止」做成接口上的第二个方法，等于让每个调用方都要考虑「我可能被反悔」，
/// 而真正需要它的只有一处。
/// </para>
/// </remarks>
public interface IApplicationLifetime
{
    /// <summary>
    /// 请求退出程序。
    /// </summary>
    /// <remarks>
    /// 调用之后当前这一帧还会跑完，退出要到消息泵空下来才真正发生。
    /// <strong>不要在这之后写「退出后才会执行」的代码</strong>——那句话是假的，
    /// 它会在退出之前就执行。
    /// </remarks>
    void RequestShutdown();
}
