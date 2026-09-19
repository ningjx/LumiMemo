using System.IO;
using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.Core.Events;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.App.Tests.Services;

/// <summary>
/// <see cref="FileWatchService"/> 的单元测试：去抖、去重、自写抑制、溢出恢复（§10.2–§10.4）。
/// </summary>
/// <remarks>
/// <para>
/// 时间由 <see cref="RecordingUiTimerFactory"/> 手工推动，「事件什么时候到」由
/// <see cref="FakeFileWatcher"/> 决定——真 <c>FileSystemWatcher</c> 会不会报某个事件是集成测试的事
/// （§21.3），这里验的是<strong>报告之后的处理</strong>。
/// </para>
/// <para>
/// 收尾那一段要 <c>await harness.Service.PendingWork</c>：定时器到期的回调是 <c>void</c>，
/// 读盘又走 <c>Task.Run</c>，无消息泵的测试进程里它是唯一观察得到的落点。
/// </para>
/// </remarks>
public sealed class FileWatchServiceTests
{
    private const string FirstPath = @"D:\notes\第一张.md";
    private const string SecondPath = @"D:\notes\第二张.md";
    private const string ThirdPath = @"D:\notes\第三张.md";

    [Fact]
    public async Task 窗口内的多个事件_合并成一批只交一次()
    {
        // 一次批量操作（git 切分支、同步盘刷一批）会连着报几十个事件。
        // 每一个都单独走一遍「读盘 + 交给界面」既慢又会让界面抖，所以要合并。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, SecondPath);
        harness.Watcher.Raise(FileWatchChangeKind.Created, ThirdPath);
        Settle(harness);
        await WorkAsync(harness);

        Assert.Single(harness.Sink.Batches);
        Assert.Equal([FirstPath, SecondPath, ThirdPath], harness.Sink.AllPaths);
    }

    [Fact]
    public void 每来一个事件_窗口就重新计时()
    {
        // 「合并成一批」这条断言，一个把回调攒起来一次跑三遍、或者窗口不重置的实现也能通过。
        // 补这一条钉住「重新计时」：只有一个定时器，被启动了三次。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, SecondPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, ThirdPath);

        Assert.Single(harness.Timers.Created);
        Assert.Equal(3, harness.Timer.StartCount);
        Assert.Equal(TimeSpan.FromMilliseconds(FileWatchService.DebounceMilliseconds), harness.Timer.Interval!.Value);
    }

    [Fact]
    public async Task 同一条路径被报两次_只读一次盘()
    {
        // 一次保存往往同时触发 Changed 与 Size，同一条路径短时间内被报多次是常态。
        // 不去重的话，一次编辑要读两次盘、进两次批次，而第二次读到的内容与第一次一模一样。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        Settle(harness);
        await WorkAsync(harness);

        Assert.Equal([FirstPath], harness.Repository.ReloadedPaths);
        Assert.Equal([FirstPath], harness.Sink.AllPaths);
    }

    [Fact]
    public async Task 磁盘没变的_不进管理器()
    {
        // 自写抑制落在这一句上（§10.3）：我们刚保存完，监听器报回来的那个事件
        // 读出来与上次同步的字节一模一样。它要是进了管理器，就成了一次假的「外部修改」。
        using Harness harness = CreateHarness();
        harness.Repository.ReloadResults[FirstPath] = new NoteFileSync(NewNote(FirstPath), DiskChanged: false);

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        Settle(harness);
        await WorkAsync(harness);

        // 盘读过了——判据本来就是「读完之后比字节」，不读就无从判断。
        Assert.Equal([FirstPath], harness.Repository.ReloadedPaths);
        Assert.Empty(harness.Sink.Batches);
    }

    [Fact]
    public async Task 重命名_新路径排在旧路径前面()
    {
        // 顺序反了的话，旧路径那一遍会先把这张便签从 Store 里摘走，
        // 新路径那一遍就找不到同一个 id，于是「同一张便签换了个路径」
        // 退化成「删掉一张、新增一张」——开着的那扇窗成了孤儿。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Renamed, SecondPath, FirstPath);
        Settle(harness);
        await WorkAsync(harness);

        Assert.Equal([SecondPath, FirstPath], harness.Sink.AllPaths);
    }

    [Fact]
    public async Task 有一条读不出来_其余的照常进批次()
    {
        // 文件被别的程序独占锁住是日常（§5.10）。那一个跳过、记一笔，
        // 但绝不能因此把同一批里其他几条便签的变更一起丢掉。
        using Harness harness = CreateHarness();
        harness.Repository.ReloadFailures[SecondPath] = new IOException("文件被占用");

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, SecondPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, ThirdPath);
        Settle(harness);
        await WorkAsync(harness);

        Assert.Equal([FirstPath, ThirdPath], harness.Sink.AllPaths);
    }

    [Fact]
    public async Task 缓冲区溢出_重建监听器并重扫整个目录()
    {
        // §10.4：溢出之后监听器不自愈，那一段的事件是真丢了。
        // 能补回来的只有「有哪些文件、长什么样」，所以重扫是唯一出路。
        using Harness harness = CreateHarness();
        harness.Service.Start();

        harness.Watcher.Raise(FileWatchChangeKind.Error, FirstPath);
        Settle(harness);
        await WorkAsync(harness);

        // 重建：先 Stop 再 Start，就是把内部那个 FileSystemWatcher 换掉。
        Assert.Equal(1, harness.Watcher.StopCount);
        Assert.Equal(2, harness.Watcher.StartCount);

        Assert.Equal(1, harness.Sink.FullRescanCount);

        // 溢出不带路径——「谁动过」这条信息恰恰是丢掉的那一部分。
        Assert.Empty(harness.Sink.Batches);
    }

    [Fact]
    public void 溢出之后_普通事件不把重扫往后推()
    {
        // 溢出之后往往还跟着一大批刚落地的文件事件。那些事件要是也去重置窗口，
        // 重扫就会被一直推迟，而重扫是「重新开始监听」的前置——推迟期间监听器一直是坏的。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Error, FirstPath);
        int afterOverflow = harness.Timer.StartCount;

        harness.Watcher.Raise(FileWatchChangeKind.Changed, SecondPath);

        Assert.Equal(afterOverflow, harness.Timer.StartCount);
        Assert.Equal(TimeSpan.FromMilliseconds(FileWatchService.OverflowDebounceMilliseconds), harness.Timer.Interval!.Value);
    }

    [Fact]
    public async Task 溢出之后的普通事件_仍然进得去这一批()
    {
        // 上一条钉的是「不重置窗口」，但那些事件本身不能丢：它们带来的是
        // 溢出之后新发生的改动，而重扫补得回内容、补不回「刚才谁动过它」。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Error, FirstPath);
        harness.Watcher.Raise(FileWatchChangeKind.Changed, SecondPath);
        Settle(harness);
        await WorkAsync(harness);

        Assert.Equal([SecondPath], harness.Sink.AllPaths);
        Assert.Equal(1, harness.Sink.FullRescanCount);
    }

    [Fact]
    public void 外部变更走低优先级那一档()
    {
        // §10.6：Normal 比 Render 还高，一批外部变更排在那里会把用户的输入挤在后面
        // （界面照样对，只是卡）。这条约定漏掉时没有别的症状，只能靠计数钉住。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);

        // 第一跳：线程池线程上的原始事件封送回 UI 线程去收集路径。
        Assert.Equal(1, harness.Dispatcher.BackgroundInvokeCount);
    }

    [Fact]
    public void 释放之后_不再响应事件()
    {
        // 退出流程在走，此刻监听器还挂着的话，一条迟到的通知会去开窗、动 Store——
        // 那些东西正在被拆。这里连去抖都不该被重新启动。
        using Harness harness = CreateHarness();

        harness.Service.Dispose();
        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);

        Assert.Empty(harness.Sink.Batches);
        Assert.Empty(harness.Repository.ReloadedPaths);
        Assert.False(harness.Timer.IsRunning);
    }

    [Fact]
    public void 释放之后_定时器不再惦记着回调()
    {
        // 退出流程在走，此刻还留着一条会在几百毫秒后醒来的定时器，
        // 那条回调会去碰一个已经在拆的进程。
        using Harness harness = CreateHarness();

        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);
        harness.Service.Dispose();

        Assert.False(harness.Timer.IsRunning);
    }

    [Fact]
    public void 重复启动_只订一次事件()
    {
        // 启动路径（StartupSequence）与溢出恢复（Stop + Start）都会调它，
        // 不幂等的话第二次会订上第二份回调，每个事件处理两遍。
        using Harness harness = CreateHarness();

        harness.Service.Start();
        harness.Service.Start();
        harness.Watcher.Raise(FileWatchChangeKind.Changed, FirstPath);

        Assert.Equal(1, harness.Dispatcher.BackgroundInvokeCount);
        Assert.Equal(1, harness.Watcher.StartCount);
    }

    // ---- 辅助 ----

    private static Harness CreateHarness()
    {
        var watcher = new FakeFileWatcher();
        var repository = new FakeNoteRepository();
        var sink = new FakeExternalChangeSink();
        var dispatcher = new RecordingDispatcher();
        var timers = new RecordingUiTimerFactory();

        var service = new FileWatchService(
            watcher, repository, sink, dispatcher, timers, NullLogger<FileWatchService>.Instance);

        service.Start();

        return new Harness(service, watcher, repository, sink, dispatcher, timers);
    }

    /// <summary>让去抖窗口到期。</summary>
    private static void Settle(Harness harness) => harness.Timer.Fire();

    /// <summary>等这一批处理完（读盘走 <c>Task.Run</c>，只能从 <see cref="FileWatchService.PendingWork"/> 上等）。</summary>
    private static async Task WorkAsync(Harness harness)
    {
        Task? work = harness.Service.PendingWork;
        Assert.NotNull(work);
        await work;
    }

    private static Note NewNote(string path)
    {
        var id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = path,
            Content = "正文",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private sealed record Harness(
        FileWatchService Service,
        FakeFileWatcher Watcher,
        FakeNoteRepository Repository,
        FakeExternalChangeSink Sink,
        RecordingDispatcher Dispatcher,
        RecordingUiTimerFactory Timers) : IDisposable
    {
        public RecordingUiTimer Timer => Timers.Last;

        public void Dispose() => Service.Dispose();
    }
}
