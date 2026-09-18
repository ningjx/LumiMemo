using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IDispatcher"/> 的测试实现：就地同步执行，不做任何封送（§21.1）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义是让 ViewModel 的异步逻辑在单元测试里<strong>确定性</strong>地跑完——
/// 不需要真实的 WPF <c>Dispatcher</c>，也不需要靠等待去猜什么时候执行完。
/// </para>
/// <para>
/// 它记录一个「UI 线程」标识，<see cref="VerifyAccess"/> 依然会真的检查。
/// 这样「本该在 UI 线程上做的事跑到了线程池线程」这类错误在测试里依然会被抓到，
/// 而不是因为用了替身就把断言一起替身掉了（§3.4 规则 T1、T5）。
/// </para>
/// </remarks>
public sealed class ImmediateDispatcher : IDispatcher
{
    private readonly int _uiThreadId;

    /// <summary>把构造它的那个线程当作 UI 线程。</summary>
    public ImmediateDispatcher()
        : this(Environment.CurrentManagedThreadId)
    {
    }

    /// <param name="uiThreadId">充当 UI 线程的托管线程 id。用于构造「非 UI 线程调用」的测试场景。</param>
    public ImmediateDispatcher(int uiThreadId) => _uiThreadId = uiThreadId;

    /// <inheritdoc />
    public void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException(
                $"当前线程 {Environment.CurrentManagedThreadId} 不是 UI 线程 {_uiThreadId}。");
        }
    }

    /// <inheritdoc />
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        action();
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        action();

        return Task.CompletedTask;
    }
}
