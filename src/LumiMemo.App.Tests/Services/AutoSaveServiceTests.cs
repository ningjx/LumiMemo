using System.IO;
using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.App.Tests.Services;

/// <summary>
/// <see cref="AutoSaveService"/> 的单元测试：去抖合并与退出时的收尾（§11.3、§17.4）。
/// </summary>
/// <remarks>
/// 全部用例都靠 <see cref="RecordingUiTimerFactory"/> 手工推动时间——
/// 真实的 500 毫秒在测试里既慢又不确定，而这里要验的恰恰是「到那一刻做了什么」。
/// </remarks>
public sealed class AutoSaveServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void 连续排五次_只保存一次()
    {
        // 用户连打十个字符会触发十次 ScheduleSave，而磁盘只该被碰一次。
        // 这是 §11.3 去抖的全部意义。
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            harness.Service.ScheduleSave(noteId);
        }

        harness.Timers.Last.Fire();

        Assert.Single(harness.Notes.SavedNoteIds);
        Assert.Equal(noteId, harness.Notes.SavedNoteIds[0]);
    }

    [Fact]
    public void 连续排五次_是重新计时而不是攒下五个回调()
    {
        // 「只保存一次」这条断言，一个把回调攒起来一次跑五遍的实现也能通过。
        // 补这一条把两种实现区分开：定时器只该被建一个、被重启五次。
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            harness.Service.ScheduleSave(noteId);
        }

        harness.Timers.Last.Fire();

        Assert.Single(harness.Timers.Created);
        Assert.Equal(5, harness.Timers.Last.StartCount);
    }

    [Fact]
    public void 默认去抖时长是五百毫秒()
    {
        var harness = CreateHarness();

        harness.Service.ScheduleSave(Guid.NewGuid());

        Assert.Equal(TimeSpan.FromMilliseconds(500), harness.Timers.Last.Interval!.Value);
    }

    [Fact]
    public void 去抖时长跟着设置走()
    {
        var harness = CreateHarness();
        harness.Service.DelayMilliseconds = 300;

        harness.Service.ScheduleSave(Guid.NewGuid());

        Assert.Equal(TimeSpan.FromMilliseconds(300), harness.Timers.Last.Interval!.Value);
    }

    [Fact]
    public void 保存之后再次编辑_还能再存一次()
    {
        // 一个「只能存一次」的实现能让上面那条断言通过，因此必须补这一条。
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();

        harness.Service.ScheduleSave(noteId);
        harness.Timers.Last.Fire();

        harness.Service.ScheduleSave(noteId);
        harness.Timers.Last.Fire();

        Assert.Equal(2, harness.Notes.SavedNoteIds.Count);
    }

    [Fact]
    public void 两张便签各等各的去抖()
    {
        // 便签是一张一个文件，同时编辑两张必须各算各的 500 毫秒。
        // 共用一个定时器的话，编辑 B 会把 A 的保存一直往后推。
        var harness = CreateHarness();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        harness.Service.ScheduleSave(first);
        harness.Service.ScheduleSave(second);

        Assert.Equal(2, harness.Timers.Created.Count);
    }

    [Fact]
    public void 取消之后_定时器到期也不再保存()
    {
        // §18.3：便签窗口关闭时必须取消，否则 Window 关了还会触发一次保存，
        // 而那时 ViewModel 已经释放。
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();

        harness.Service.ScheduleSave(noteId);
        harness.Service.CancelScheduledSave(noteId);
        harness.Timers.Last.Fire();

        Assert.Empty(harness.Notes.SavedNoteIds);
    }

    [Fact]
    public async Task 立即保存_绕过去抖并取消等待中的那一轮()
    {
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();

        harness.Service.ScheduleSave(noteId);
        await harness.Service.SaveNowAsync(noteId);

        Assert.Single(harness.Notes.SavedNoteIds);

        // 已经存过了，那个还在等的定时器不该再存一遍。
        harness.Timers.Last.Fire();
        Assert.Single(harness.Notes.SavedNoteIds);
    }

    [Fact]
    public async Task 全部落盘_把每张待保存的便签都存一遍()
    {
        // 退出流程走这条（§17.4）：等不了 500 毫秒，必须此刻写掉。
        var harness = CreateHarness();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        harness.Service.ScheduleSave(first);
        harness.Service.ScheduleSave(second);

        await harness.Service.FlushAllAsync(Ct);

        Assert.Equal(2, harness.Notes.SavedNoteIds.Count);
        Assert.Contains(first, harness.Notes.SavedNoteIds);
        Assert.Contains(second, harness.Notes.SavedNoteIds);
    }

    [Fact]
    public void 保存失败_只记日志不往外抛()
    {
        // 这是个后台去抖动作，此刻用户可能正在别的窗口打字。
        // 让异常从定时器回调里逃出去，轻则静默失败，重则把整个进程带走。
        var harness = CreateHarness();
        harness.Notes.SaveException = new IOException("文件被占用");

        harness.Service.ScheduleSave(Guid.NewGuid());

        // 这一行本身不抛，就是断言。
        harness.Timers.Last.Fire();
    }

    [Fact]
    public void 释放之后_定时器不再惦记着回调()
    {
        var harness = CreateHarness();

        harness.Service.ScheduleSave(Guid.NewGuid());
        harness.Service.Dispose();

        Assert.False(harness.Timers.Last.IsRunning);
    }

    // ---- 辅助 ----

    private static Harness CreateHarness()
    {
        var notes = new FakeNoteService();
        var timers = new RecordingUiTimerFactory();
        var service = new AutoSaveService(notes, timers, NullLogger<AutoSaveService>.Instance);

        return new Harness(service, notes, timers);
    }

    private sealed record Harness(
        AutoSaveService Service,
        FakeNoteService Notes,
        RecordingUiTimerFactory Timers);
}
