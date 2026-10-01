using System.IO;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.WinUI.Tests.Services;

/// <summary>
/// <see cref="AutoSaveService"/> 的去抖与冲刷（§11.3）。
/// </summary>
/// <remarks>
/// 定时器全部走 <see cref="ManualUiTimerFactory"/> 手动到期，断言是确定性的。
/// </remarks>
public sealed class AutoSaveServiceTests
{
    [Fact]
    public void 连续安排只落盘一次()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);
        Guid id = Guid.NewGuid();
        int persistCount = 0;

        service.ScheduleSave(id, () => { persistCount++; return Task.CompletedTask; });
        service.ScheduleSave(id, () => { persistCount++; return Task.CompletedTask; });
        service.ScheduleSave(id, () => { persistCount++; return Task.CompletedTask; });

        // 同一张便签只造一个定时器；每次安排都是「重新计时」。
        ManualUiTimer timer = Assert.Single(timers.Created);
        Assert.Equal(3, timer.StartCount);
        Assert.Equal(0, persistCount);

        timer.Tick();

        Assert.Equal(1, persistCount);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public void 后来的委托替换先前的()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);
        Guid id = Guid.NewGuid();
        bool firstRan = false;
        bool secondRan = false;

        service.ScheduleSave(id, () => { firstRan = true; return Task.CompletedTask; });
        service.ScheduleSave(id, () => { secondRan = true; return Task.CompletedTask; });
        timers.Created[0].Tick();

        Assert.False(firstRan);
        Assert.True(secondRan);
    }

    [Fact]
    public void 取消之后不再触发且条目被清掉()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);
        Guid id = Guid.NewGuid();
        bool ran = false;

        service.ScheduleSave(id, () => { ran = true; return Task.CompletedTask; });
        service.CancelScheduledSave(id);

        // 条目移除 + 定时器释放：闭包（持有 ViewModel 与编辑区）不该被留在一个死条目里。
        Assert.Equal(0, service.PendingCount);
        Assert.True(timers.Created[0].IsDisposed);

        timers.Created[0].Tick();
        Assert.False(ran);
    }

    [Fact]
    public async Task 立刻保存绕过去抖()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);
        Guid id = Guid.NewGuid();
        int persistCount = 0;

        service.ScheduleSave(id, () => { persistCount++; return Task.CompletedTask; });
        await service.SaveNowAsync(id);

        Assert.Equal(1, persistCount);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task 冲刷全部_把在等的都写掉()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);
        int first = 0;
        int second = 0;

        service.ScheduleSave(Guid.NewGuid(), () => { first++; return Task.CompletedTask; });
        service.ScheduleSave(Guid.NewGuid(), () => { second++; return Task.CompletedTask; });

        await service.FlushAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public void 委托抛IO异常_被吞掉不炸出去()
    {
        var timers = new ManualUiTimerFactory();
        using var service = Create(timers);

        service.ScheduleSave(Guid.NewGuid(), () => Task.FromException(new IOException("磁盘满了")));

        // 手动到期是同步路径：异常在这里就没被吞的话，测试直接红。
        Exception? failure = Record.Exception(() => timers.Created[0].Tick());

        Assert.Null(failure);
    }

    private static AutoSaveService Create(ManualUiTimerFactory timers) =>
        new(timers, NullLogger<AutoSaveService>.Instance);
}
