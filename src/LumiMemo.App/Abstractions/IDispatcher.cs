namespace LumiMemo.App.Abstractions;

/// <summary>
/// 把「在 UI 线程上执行」这件事抽象出来，使 ViewModel 可以在没有 WPF 线程的测试进程里运行（§21.1）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的唯一理由是测试。生产代码里它就是对 <c>System.Windows.Threading.Dispatcher</c> 的转发，
/// 没有任何加工。
/// </para>
/// <para>
/// 为什么 ViewModel 不能直接调 <c>Application.Current.Dispatcher</c>：
/// 单元测试进程里没有 <see cref="System.Windows.Application"/>，
/// <c>Application.Current</c> 是 <c>null</c>，于是「能测」这件事就没了。
/// </para>
/// <para>
/// 反过来，也绝不允许 ViewModel 因为「测试里不需要」就跳过封送。
/// FileSystemWatcher 的回调在线程池线程上（§3.4 规则 T3），
/// 少了封送就是跨线程改 <c>ObservableCollection</c>，
/// 表现为随机的 <c>NotSupportedException</c> 或界面错乱（§3.4 规则 T5）。
/// </para>
/// </remarks>
public interface IDispatcher
{
    /// <summary>
    /// 当前线程就是 UI 线程时返回，否则抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <remarks>
    /// 用于断言「这段代码只应该在 UI 线程上跑」（§3.4 规则 T1、T5）。
    /// 这类断言在开发期抓错，比在生产期抓崩溃便宜得多。
    /// </remarks>
    void VerifyAccess();

    /// <summary>当前线程是否为 UI 线程。需要条件性封送时使用。</summary>
    bool CheckAccess();

    /// <summary>同步在 UI 线程上执行。已在 UI 线程时直接执行。</summary>
    /// <remarks>
    /// 只用在确定不会死锁的场合。UI 线程在等后台线程时，
    /// 后台线程再同步回 UI 线程就是死锁（§3.4）。
    /// 那种情形用 <see cref="InvokeAsync"/>。
    /// </remarks>
    void Invoke(Action action);

    /// <summary>异步在 UI 线程上执行，返回可等待的任务。</summary>
    /// <remarks>
    /// §3.4 规则 T6 的落点：文件 IO 在线程池线程上跑完，
    /// 结果通过这个方法回到 UI 线程再写进 <c>NoteStore</c>。
    /// </remarks>
    Task InvokeAsync(Action action);
}
