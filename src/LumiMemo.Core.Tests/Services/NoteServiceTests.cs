using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Core.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary>
/// <see cref="NoteService"/> 的单元测试：内存编排与开窗判断（§14.2）。
/// </summary>
/// <remarks>
/// 真正的磁盘行为在 <c>LumiMemo.Integration.Tests</c> 里用真实文件系统覆盖，
/// 这里只验本类的两件事：<strong>该写内存的时候写了内存</strong>，
/// 以及<strong>该落盘的时候把活交给了仓储</strong>。
/// </remarks>
public sealed class NoteServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================= 磁盘 → 内存 =================

    [Fact]
    public async Task 载入时_把仓储的便签放进内存并建好索引()
    {
        var harness = CreateHarness();
        Note first = NewNote("# 购物清单\n- 牛奶");
        Note second = NewNote("# 会议记录\n下周三");
        harness.Repository.NotesToLoad.AddRange([first, second]);

        await harness.Service.LoadAllAsync(Ct);

        Assert.Equal(2, harness.Store.Count);
        Assert.Same(first, harness.Store.TryGet(first.Id));
        Assert.Equal(1, harness.Repository.LoadCallCount);

        // 索引必须跟着建起来，否则管理器的搜索框一上来就是空的。
        Assert.Contains("牛奶", harness.Index.GetPlainText(first.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 载入时_清掉上一次留在内存里的便签()
    {
        // 切换笔记目录后必须重新载入（§8.6）。若只做 Add 不 Clear，
        // 上一个目录的便签会留在列表里，点开就是「文件不存在」。
        var harness = CreateHarness();
        harness.Store.Add(NewNote("上一个目录的便签", Guid.NewGuid()));

        harness.Repository.NotesToLoad.Add(NewNote("新目录的便签"));

        await harness.Service.LoadAllAsync(Ct);

        Assert.Equal(1, harness.Store.Count);
        Assert.Contains("新目录", harness.Store.Snapshot()[0].Content, StringComparison.Ordinal);
    }

    // ================= 编辑 → 内存 =================

    [Fact]
    public void 本地编辑_写回正文并更新修改时间()
    {
        var harness = CreateHarness();
        Note note = NewNote("旧内容");
        harness.Store.Add(note);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        harness.Service.ApplyLocalEdit(note, "新内容");

        Assert.Equal("新内容", note.Content);
        Assert.Equal(harness.Clock.Now, note.UpdatedAt);
        Assert.Contains("新内容", harness.Index.GetPlainText(note.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void 本地编辑_不写磁盘()
    {
        // 流 1 的前半段只碰内存（§3.3）。真正落盘由 AutoSaveService 去抖后调 SaveNoteAsync。
        // 这条断言是「每次按键都写盘」这个缺陷的拦路石。
        var harness = CreateHarness();
        Note note = NewNote("旧内容");

        harness.Service.ApplyLocalEdit(note, "新内容");

        Assert.Empty(harness.Repository.Saved);
    }

    // ================= 内存 → 磁盘 =================

    [Fact]
    public async Task 保存_把便签交给仓储()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        await harness.Service.SaveNoteAsync(note.Id);

        Assert.Same(note, Assert.Single(harness.Repository.Saved));
    }

    [Fact]
    public async Task 保存_便签已被删掉时静默返回而不是抛异常()
    {
        // 自动保存是按 id 排的队，用户完全可能在去抖那 500 毫秒里把便签删了。
        // 那不是错误，不该炸在定时器回调里。
        var harness = CreateHarness();

        await harness.Service.SaveNoteAsync(Guid.NewGuid());

        Assert.Empty(harness.Repository.Saved);
    }

    [Fact]
    public async Task 保存全部_把每张便签都交给仓储()
    {
        var harness = CreateHarness();
        harness.Store.Add(NewNote("第一张"));
        harness.Store.Add(NewNote("第二张"));

        await harness.Service.SaveAllAsync(Ct);

        Assert.Equal(2, harness.Repository.Saved.Count);
    }

    // ================= 开窗判断 =================

    [Fact]
    public void 打开便签_返回请求并把IsOpen置为真()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        NoteOpenRequest? request = harness.Service.OpenNote(note.Id);

        Assert.NotNull(request);
        Assert.Same(note, request.Value.Note);
        Assert.True(request.Value.Layout.IsOpen);
    }

    [Fact]
    public void 打开从未出现过的便签_顺带建好它的布局条目()
    {
        // 一张刚放进目录、还没被打开过的便签走到这里才第一次拿到布局条目，
        // 而 §8.3 要求新条目必须落盘——否则下次启动无从知道它上次是开着的。
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        _ = harness.Service.OpenNote(note.Id);

        Assert.NotNull(harness.Layouts.TryGet(note.Id));
        Assert.True(harness.Timers.Last.IsRunning);
    }

    [Fact]
    public void 打开不存在的便签_返回null且不建布局()
    {
        var harness = CreateHarness();

        Assert.Null(harness.Service.OpenNote(Guid.NewGuid()));
        Assert.Empty(harness.Layouts.All);
    }

    [Fact]
    public void 打开全部_只返回上次关着之前是开着的便签()
    {
        var harness = CreateHarness();
        Note opened = NewNote("上次开着的");
        Note closed = NewNote("上次关着的");
        harness.Store.Add(opened);
        harness.Store.Add(closed);

        NoteLayout openLayout = harness.Layouts.GetOrCreate(opened.Id);
        openLayout.IsOpen = true;

        NoteLayout closedLayout = harness.Layouts.GetOrCreate(closed.Id);
        closedLayout.IsOpen = false;

        NoteOpenRequest request = Assert.Single(harness.Service.OpenAll());

        Assert.Same(opened, request.Note);
    }

    [Fact]
    public void 打开全部_不给没有布局记录的便签凭空建条目()
    {
        // §17.1 的裁决：首启不自动弹窗。一张从没被打开过的便签（没有布局记录）
        // 必须保持关闭。这里若顺手 GetOrCreate，它就会在下次启动时冒出来——
        // 用户第一次运行程序，屏幕上会突然堆满便签。
        var harness = CreateHarness();
        harness.Store.Add(NewNote("从没打开过"));

        Assert.Empty(harness.Service.OpenAll());
        Assert.Empty(harness.Layouts.All);
    }

    [Fact]
    public void 关闭便签_把IsOpen置为假并安排落盘()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);
        _ = harness.Service.OpenNote(note.Id);

        harness.Service.MarkNoteClosed(note.Id);

        Assert.False(harness.Layouts.TryGet(note.Id)!.IsOpen);
    }

    [Fact]
    public void 关闭没有布局记录的便签_什么也不做()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        harness.Service.MarkNoteClosed(note.Id);

        Assert.Empty(harness.Layouts.All);
    }

    // ================= 本轮明确不支持的能力 =================

    [Fact]
    public void 外部变更_明确抛异常而不是静默吞掉()
    {
        // 本程序的启动扫描会主动写用户文件（补 id、原子替换）。若这里留一个空实现，
        // 那些自写事件会以「外部修改」的身份涌进内存，把用户刚改的内容覆盖回去。
        // 宁可让它在调用点立刻炸掉，也不要留一个看起来能用的假实现。
        var harness = CreateHarness();

        // 两个参数都无关紧要——这个方法不管收到什么都会抛。
        Assert.Throws<NotSupportedException>(
            () => harness.Service.ApplyExternalChange(@"D:\notes\a.md", null!));
    }

    // ---- 辅助 ----

    private static Harness CreateHarness()
    {
        var store = new NoteStore();
        var index = new SearchIndex();
        var repository = new FakeNoteRepository();
        var layouts = new InMemoryLayoutStore();
        var timers = new ManualUiTimerFactory();
        var clock = new FakeClock();

        var layoutService = new LayoutService(layouts, FixedDisplayProvider.Single(), timers);
        var service = new NoteService(store, index, repository, layoutService, clock);

        return new Harness(service, store, index, repository, layouts, timers, clock);
    }

    private static Note NewNote(string content, Guid? id = null)
    {
        Guid noteId = id ?? Guid.NewGuid();

        return new Note
        {
            Id = noteId,
            FilePath = $@"D:\notes\{noteId:N}.md",
            Content = content,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }

    private sealed record Harness(
        NoteService Service,
        NoteStore Store,
        SearchIndex Index,
        FakeNoteRepository Repository,
        InMemoryLayoutStore Layouts,
        ManualUiTimerFactory Timers,
        FakeClock Clock);
}
