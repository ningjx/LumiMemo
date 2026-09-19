using LumiMemo.App.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 只记账的 <see cref="IExternalChangeSink"/>：监听那套逻辑验的是「交出去了什么」。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它刻意不调 <c>IDispatcher.VerifyAccess</c></strong>，与真实现
/// （<c>ManagerViewModel</c>）不一样。<c>FileWatchService</c> 把结果封送回 UI 线程的那一步
/// 走的是 <c>Task.Run</c> 之后的续体，在测试进程里落在<strong>线程池线程</strong>上；
/// 而 <see cref="ImmediateDispatcher"/> 是就地执行的替身，它封送不了任何东西。
/// 这里若照着真实现做线程校验，「监听器有没有封送」这条断言就只能靠装一整套
/// WPF 消息泵来验——那是集成测试的代价，不该由这个替身承担。
/// </para>
/// <para>
/// 真实现那边「必须在 UI 线程上」这条约定由 <c>ManagerViewModel</c> 自己守
/// （它一进门就 <c>VerifyAccess</c>），并由 <c>ManagerViewModelTests</c> 覆盖。
/// </para>
/// </remarks>
public sealed class FakeExternalChangeSink : IExternalChangeSink
{
    /// <summary>收到过的批次，按发生顺序。每个批次内部保持送达时的顺序。</summary>
    public List<IReadOnlyList<(string Path, NoteFileSync Sync)>> Batches { get; } = [];

    /// <summary><see cref="ApplyFullRescanAsync"/> 被调了几次。溢出恢复靠它验。</summary>
    public int FullRescanCount { get; private set; }

    /// <summary>把 <see cref="Batches"/> 里所有批次摊平成一个路径序列，断言顺序时好用。</summary>
    public List<string> AllPaths => [.. Batches.SelectMany(batch => batch.Select(item => item.Path))];

    /// <inheritdoc />
    public Task ApplyExternalChangesAsync(IReadOnlyList<(string Path, NoteFileSync Sync)> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        Batches.Add(batch);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ApplyFullRescanAsync()
    {
        FullRescanCount++;

        return Task.CompletedTask;
    }
}
