using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 行为与 <see cref="ImmediateDispatcher"/> 完全相同，另把每一次「让出一帧」记下来。
/// </summary>
/// <remarks>
/// <para>
/// 分批开窗口（§17.1 第 11 步）的判据是<strong>批次之间有没有真的松手</strong>，
/// 而不是最后开出来几张——一个把二十扇窗一口气开完的实现也能开出同样的二十张。
/// 所以这里记的不是次数，而是<see cref="OnYield"/>：让调用方在那一刻自己取一份快照。
/// </para>
/// <para>
/// 快照与 <see cref="YieldOrder"/> 一起，就能把「第一批三个、之后每批两个」这条策略
/// 钉死成 <c>[3, 5, 7]</c> 这样一串具体数字。
/// </para>
/// </remarks>
public sealed class RecordingDispatcher : IDispatcher
{
    private readonly ImmediateDispatcher _inner = new();

    /// <summary>让出之前要做的事。调用方用它记下「那时已经开到哪里了」。</summary>
    public Action? OnYield { get; set; }

    /// <summary>每一次 <see cref="YieldAsync"/> 留下的记号，按发生顺序。</summary>
    public List<string> YieldOrder { get; } = [];

    /// <summary>
    /// 走低优先级封送进来的次数。
    /// </summary>
    /// <remarks>
    /// 两个 <c>InvokeBackgroundAsync</c> 与 <see cref="InvokeAsync"/> 在本替身里行为完全一样，
    /// 都要靠这个计数才分得出来。§10.6 要求外部变更走低优先级那一档
    /// （<c>Normal</c> 比 <c>Render</c> 还高，一批变更会把用户的输入挤在后面），
    /// 而这条约定漏掉时没有别的症状——界面照样对，只是卡。
    /// </remarks>
    public int BackgroundInvokeCount { get; private set; }

    /// <inheritdoc />
    public void VerifyAccess() => _inner.VerifyAccess();

    /// <inheritdoc />
    public bool CheckAccess() => _inner.CheckAccess();

    /// <inheritdoc />
    public void Invoke(Action action) => _inner.Invoke(action);

    /// <inheritdoc />
    public Task InvokeAsync(Action action) => _inner.InvokeAsync(action);

    /// <inheritdoc />
    public Task InvokeBackgroundAsync(Action action)
    {
        BackgroundInvokeCount++;

        return _inner.InvokeBackgroundAsync(action);
    }

    /// <inheritdoc />
    public Task InvokeBackgroundAsync(Func<Task> action)
    {
        BackgroundInvokeCount++;

        return _inner.InvokeBackgroundAsync(action);
    }

    /// <inheritdoc />
    public Task YieldAsync()
    {
        OnYield?.Invoke();
        YieldOrder.Add($"yield#{YieldOrder.Count + 1}");

        return Task.CompletedTask;
    }
}
