using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary>
/// <see cref="LayoutService"/> 的单元测试：一秒去抖与放置转交（§8.5、§13.8）。
/// </summary>
/// <remarks>
/// 放置算法本身的边界条件在 <c>LayoutMathTests</c> 里逐条钉过了，这里只验本类自己的两件事：
/// <strong>节流真的合并了</strong>，以及<strong>它确实把当前显示器与状态条开关交给了算法</strong>。
/// </remarks>
public sealed class LayoutServiceTests
{
    private const string PrimaryId = @"\\.\DISPLAY1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================= 落盘节流（§8.5） =================

    [Fact]
    public async Task 载入布局转交给存储层()
    {
        (LayoutService service, InMemoryLayoutStore store, _) = CreateService();

        await service.LoadAsync(Ct);

        Assert.Equal(1, store.LoadCount);
    }

    [Fact]
    public void 去抖时长默认一秒()
    {
        (LayoutService service, _, ManualUiTimerFactory timers) = CreateService();

        service.MarkDirtyAndScheduleFlush();

        Assert.Equal(TimeSpan.FromSeconds(1), timers.Last.Interval!.Value);
    }

    [Fact]
    public void 连续标记五次只落盘一次()
    {
        // 拖动一次窗口会产生几十次几何变化，每一次都会标记脏。
        // 这条断言是「拖动期间绝不写磁盘」的全部证据（§8.5）。
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        for (int i = 0; i < 5; i++)
        {
            service.MarkDirtyAndScheduleFlush();
        }

        timers.Last.Fire();

        Assert.Equal(1, store.FlushCount);
    }

    [Fact]
    public void 连续标记是重新计时而不是排队等多次回调()
    {
        // 排队的实现会攒下 5 个回调，一次到期跑 5 遍；重新计时则只会跑最后那一个。
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        for (int i = 0; i < 5; i++)
        {
            service.MarkDirtyAndScheduleFlush();
        }

        timers.Last.Fire();

        // 每次都是同一条「落盘」指令，但只应该有一个定时器被建出来、被重启五次。
        Assert.Single(timers.Created);
        Assert.Equal(5, timers.Last.StartCount);
        Assert.Equal(1, store.FlushCount);
    }

    [Fact]
    public void 落盘之后再次标记_还能再落一次盘()
    {
        // 一个「只能写一次」的实现也能让上面那条断言通过，因此必须补这一条。
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        service.MarkDirtyAndScheduleFlush();
        timers.Last.Fire();

        service.MarkDirtyAndScheduleFlush();
        timers.Last.Fire();

        Assert.Equal(2, store.FlushCount);
    }

    [Fact]
    public async Task 立即落盘_绕过去抖并取消等待中的那一轮()
    {
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        service.MarkDirtyAndScheduleFlush();
        await service.FlushNowAsync(Ct);

        Assert.Equal(1, store.FlushCount);

        // 已经写过了，那个还在等的定时器不该再写一遍。
        Assert.False(timers.Last.IsRunning);
        timers.Last.Fire();
        Assert.Equal(1, store.FlushCount);
    }

    [Fact]
    public void 新建的布局会安排一次落盘()
    {
        // §8.3：新条目必须落盘，否则下次启动不知道这张便签上次是开着的。
        // 存储层只负责把内存标脏，「什么时候真写」是这一层的事。
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        service.GetOrCreate(Guid.NewGuid());

        Assert.True(store.IsDirty);
        Assert.True(timers.Last.IsRunning);
        Assert.Equal(0, store.FlushCount);

        timers.Last.Fire();

        Assert.Equal(1, store.FlushCount);
    }

    [Fact]
    public void 释放时不落盘()
    {
        // 容器释放单例的顺序与 §17.4 的退出顺序无关，
        // 不该在这个时机插入一次未知时机的文件写。未落盘的改动由退出流程显式洗掉。
        (LayoutService service, InMemoryLayoutStore store, ManualUiTimerFactory timers) = CreateService();

        service.MarkDirtyAndScheduleFlush();
        service.Dispose();

        Assert.Equal(0, store.FlushCount);
        Assert.False(timers.Last.IsRunning);
    }

    // ================= 放置转交（§13.8） =================

    [Fact]
    public void 显示器还在时_位置尺寸原样返回()
    {
        (LayoutService service, _, _) = CreateService();
        NoteLayout saved = NewLayout(x: 100, y: 200, width: 360, height: 420);

        WindowPlacement placement = service.ResolvePlacement(saved, cascadeIndex: 0);

        Assert.Equal(new PixelRect(100, 200, 360, 420), placement.Bounds);
        Assert.Equal(PrimaryId, placement.DisplayId);
        Assert.False(placement.WasAdjusted);
    }

    [Fact]
    public void 显示器已拔掉时_层叠到主屏并标记为已修正()
    {
        // 坐标落在虚拟屏幕的空洞处，而「原显示器不在」这条判断是 LayoutService 喂进去的
        // 当前显示器清单得出的——不是照搬盘上的 DisplayId。
        (LayoutService service, _, _) = CreateService();
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);

        WindowPlacement placement = service.ResolvePlacement(saved, cascadeIndex: 0);

        Assert.Equal(PrimaryId, placement.DisplayId);
        Assert.Equal(new PixelRect(24, 24, 360, 420), placement.Bounds);
        Assert.True(placement.WasAdjusted);
    }

    [Fact]
    public void 层叠序号由调用方给_第二张会错开()
    {
        (LayoutService service, _, _) = CreateService();
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);

        WindowPlacement placement = service.ResolvePlacement(saved, cascadeIndex: 1);

        Assert.Equal(new PixelRect(52, 52, 360, 420), placement.Bounds);
    }

    [Fact]
    public void 状态条关掉时_折叠高度用标题条的高度()
    {
        // 这个开关是本类持有的运行时输入，不是常量：44 还是 36 全靠它。
        (LayoutService service, _, _) = CreateService();
        service.ShowStatusBar = false;

        NoteLayout collapsed = NewLayout(x: 100, y: 100, width: 360, height: 420);
        collapsed.IsCollapsed = true;

        Assert.Equal(36, service.ResolvePlacement(collapsed, cascadeIndex: 0).Bounds.Height);

        service.ShowStatusBar = true;

        Assert.Equal(44, service.ResolvePlacement(collapsed, cascadeIndex: 0).Bounds.Height);
    }

    [Fact]
    public void 放置不改动盘上那份布局()
    {
        // 原始坐标要留着：用户把显示器插回来之后还得靠它把窗口摆回原处（§13.8）。
        (LayoutService service, _, _) = CreateService();
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);

        service.ResolvePlacement(saved, cascadeIndex: 0);

        Assert.Equal(9000, saved.X);
        Assert.Equal(100, saved.Y);
        Assert.Equal(360, saved.Width);
    }

    // ---- 辅助 ----

    private static (LayoutService Service, InMemoryLayoutStore Store, ManualUiTimerFactory Timers) CreateService()
    {
        var store = new InMemoryLayoutStore();
        var timers = new ManualUiTimerFactory();
        var service = new LayoutService(store, FixedDisplayProvider.Single(), timers);

        return (service, store, timers);
    }

    private static NoteLayout NewLayout(double x, double y, double width, double height) => new()
    {
        NoteId = Guid.Parse("6f1d0a2e-1111-2222-3333-444455556666"),
        X = x,
        Y = y,
        Width = width,
        Height = height,
        ExpandedHeight = height,
    };
}
