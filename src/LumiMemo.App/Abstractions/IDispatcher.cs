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

    /// <summary>
    /// 异步在 UI 线程上执行，但排在<strong>低优先级</strong>上（§10.6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="InvokeAsync"/> 的差别只在<strong>排在哪一档</strong>：那个是 <c>Normal</c>，
    /// 这个走 <c>DispatcherPriority.Background</c>，而 <c>Background</c> 排在 <c>Render</c>
    /// <strong>之后</strong>。
    /// </para>
    /// <para>
    /// <strong>为什么外部变更必须让路</strong>：一批文件变化（git 切分支、同步盘刷一批）
    /// 会在几百毫秒内灌进几十条事件，每条都要改 <c>NoteStore</c>、刷列表、可能还要开窗。
    /// 用 <c>Normal</c> 排的话，这批工作会一直插在<strong>输入事件</strong>前面
    /// （<c>Input</c> 也低于 <c>Normal</c>），用户在这期间敲的键要等整批处理完才轮到——
    /// 表现为便签卡住不响应。排到 <c>Background</c>，用户的输入与重绘都排在前面。
    /// </para>
    /// </remarks>
    Task InvokeBackgroundAsync(Action action);

    /// <summary>
    /// 同 <see cref="InvokeBackgroundAsync(Action)"/>，但<strong>可以等它跑完</strong>。
    /// </summary>
    /// <remarks>
    /// 用在「排到 UI 线程的那段活本身就是异步」的场合：外部变更落到界面时要读盘、
    /// 还可能弹一个等用户选边站的冲突对话框。那种情况用上面的
    /// <see cref="InvokeBackgroundAsync(Action)"/> 的话，返回的任务发出去就没人观察了——
    /// 失败没有日志，测试也只能靠猜什么时候跑完。
    /// </remarks>
    Task InvokeBackgroundAsync(Func<Task> action);

    /// <summary>
    /// 把续体排到 UI 线程的<strong>低优先级</strong>上，让当前排队的工作先跑完。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="InvokeAsync"/> 的区别只在于<strong>排在哪一档</strong>：
    /// 这个走 <c>DispatcherPriority.Background</c>，而 <c>Background</c> 排在
    /// <c>Render</c> <strong>之后</strong>——也就是说重绘先跑，我们排在它后面等。
    /// </para>
    /// <para>
    /// <strong>为什么非要低不可</strong>：给它的用途是「一批一批地开窗口」（§17.1 第 11 步）。
    /// 一次开二十扇窗会让界面卡住几百毫秒，所以要在批次之间松手。
    /// 而若用 <see cref="InvokeAsync"/>（<c>Normal</c>，比 <c>Render</c> 高），
    /// 下一批会抢在重绘前面执行，界面照样卡——松了等于没松。
    /// </para>
    /// </remarks>
    Task YieldAsync();
}
