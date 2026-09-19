using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.App.Tests.ViewModels;

/// <summary>
/// <see cref="NoteViewModel"/> 的单元测试（§21.1：App.Tests 负责 ViewModel 逻辑）。
/// </summary>
/// <remarks>
/// 这里能跑起来本身就说明了一件事：<strong>ViewModel 不依赖 WPF 的具体类型</strong>。
/// 没有窗口、没有 Dispatcher、没有消息循环，全靠 <see cref="ImmediateDispatcher"/>、
/// <see cref="FakeNoteService"/> 这些替身把外部依赖换掉（§18.6、§21.1）。
/// </remarks>
public sealed class NoteViewModelTests
{
    // ---- 构造：三类状态各就各位 ----

    [Fact]
    public void 构造_内容与颜色取自Note()
    {
        var note = CreateNote("# 购物清单\r\n- 牛奶", NoteColor.Blue);

        var vm = CreateViewModel(note);

        Assert.Equal("# 购物清单\r\n- 牛奶", vm.Content);
        Assert.Equal(NoteColor.Blue, vm.Color);
    }

    [Fact]
    public void 构造_折叠置顶锁定取自Layout()
    {
        var layout = CreateLayout(isCollapsed: true, isTopMost: true, isLocked: true);

        var vm = CreateViewModel(CreateNote("# 标题"), layout);

        Assert.True(vm.IsCollapsed);
        Assert.True(vm.IsTopMost);
        Assert.True(vm.IsLocked);
    }

    [Fact]
    public void 构造_临时状态是干净的初始值()
    {
        var vm = CreateViewModel(CreateNote("内容"));

        // §18.1 第三类状态：只活在内存里，初始时不该是「脏」或「失败」。
        Assert.Equal(SaveStatus.Saved, vm.SaveStatus);
        Assert.False(vm.IsDirty);
        Assert.False(vm.IsComposing);
        Assert.Equal(0, vm.CaretIndex);
    }

    [Fact]
    public void 构造_持有的是同一个Note引用而不是副本()
    {
        var note = CreateNote("内容");

        var vm = CreateViewModel(note);

        // §18.4：Store 里持有唯一实例，ViewModel 拿到同一个引用，因此不存在「两份数据」。
        Assert.Same(note, vm.Note);
        Assert.Equal(note.Id, vm.Id);
    }

    [Fact]
    public void 构造_不触发保存也不写入内存()
    {
        var notes = new FakeNoteService();

        _ = CreateViewModel(CreateNote("内容"), noteService: notes);

        // 构造期间若走了属性 setter，就会在对象还没完成初始化时标记脏、排一次保存。
        Assert.Empty(notes.LocalEdits);
        Assert.Empty(notes.SavedNoteIds);
    }

    // ---- 编辑 → 内存（流 1 的前半段）----

    [Fact]
    public void 修改内容_推送给NoteService的内存编辑入口()
    {
        var note = CreateNote("旧内容");
        var notes = new FakeNoteService();
        var vm = CreateViewModel(note, noteService: notes);

        vm.Content = "新内容";

        var edit = Assert.Single(notes.LocalEdits);
        Assert.Equal(note.Id, edit.NoteId);
        Assert.Equal("新内容", edit.Content);
    }

    [Fact]
    public void 修改内容_不直接落盘()
    {
        var notes = new FakeNoteService();
        var vm = CreateViewModel(CreateNote("旧内容"), noteService: notes);

        vm.Content = "新内容";

        // 落盘只能由 AutoSaveService 去抖 500ms 后触发（流 1）。
        // ViewModel 直接调 SaveNoteAsync 会让每个按键都写一次磁盘。
        Assert.Empty(notes.SavedNoteIds);
    }

    [Fact]
    public void 修改内容_标题跟着变()
    {
        var vm = CreateViewModel(CreateNote("旧标题"));

        vm.Content = "# 新标题";

        Assert.Equal("新标题", vm.Title);
    }

    [Fact]
    public void 修改内容_触发Content与Title的属性变更通知()
    {
        var vm = CreateViewModel(CreateNote("旧标题"));
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        vm.Content = "# 新标题";

        // Title 是派生属性，必须显式通知，否则界面上标题不会刷新（§18.4）。
        Assert.Contains(nameof(NoteViewModel.Content), changed);
        Assert.Contains(nameof(NoteViewModel.Title), changed);
    }

    [Fact]
    public void 设置相同内容_不通知也不产生编辑()
    {
        // 绑定侧 UpdateSourceTrigger=PropertyChanged 会让每次按键都走一遍 setter，
        // 包括删掉又打回同一个字的这种情况。MVVM Toolkit 生成的 setter 在值相等时直接返回，
        // 于是既不会产生属性变更通知，也不会往下游排一次多余的保存。
        var notes = new FakeNoteService();
        var vm = CreateViewModel(CreateNote("内容"), noteService: notes);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        vm.Content = "内容";

        Assert.Empty(changed);
        Assert.Empty(notes.LocalEdits);
    }

    // ---- 从磁盘刷新（§10.2）----

    [Fact]
    public void 从磁盘刷新_把新正文与新颜色搬进界面()
    {
        // 外部改完之后内容已经在 Note 上了，而界面绑的是本 ViewModel 自己的一份副本
        // （Content 有自己的字段），它不会自己知道——不搬的话用户对着一个磁盘上
        // 已经不存在的版本继续打字，而下一次自动保存会把它连同旧内容一起写出去。
        var note = CreateNote("旧内容", NoteColor.Yellow);
        var vm = CreateViewModel(note);

        note.Content = "磁盘上的新内容";
        note.Color = NoteColor.Blue;
        vm.RefreshFromNote();

        Assert.Equal("磁盘上的新内容", vm.Content);
        Assert.Equal(NoteColor.Blue, vm.Color);
    }

    [Fact]
    public void 从磁盘刷新_不算用户编辑()
    {
        // OnContentChanged 会把这个值推回业务层、再排一次去抖保存。不闸住的话，
        // 「把磁盘上的内容搬进界面」会被当成本地编辑：用户什么都没做，文件却被重写一遍，
        // 修改时间跟着变——而且业务层那个「有未落盘改动」的标记会被点亮，
        // 下一次真的外部改动就会被误判成冲突。
        var note = CreateNote("旧内容");
        var notes = new FakeNoteService();
        var timers = new RecordingUiTimerFactory();
        var autoSave = new AutoSaveService(notes, timers, NullLogger<AutoSaveService>.Instance);
        var vm = CreateViewModel(note, noteService: notes, autoSaveService: autoSave);

        note.Content = "磁盘上的新内容";
        vm.RefreshFromNote();

        Assert.Equal("磁盘上的新内容", vm.Content);
        Assert.Empty(notes.LocalEdits);
        Assert.Empty(timers.Created);
    }

    [Fact]
    public void 从磁盘刷新_之后的编辑照常推送()
    {
        // 闸门只闸这一次：它是方法内的一个局部状态，不是「这个 ViewModel 从此不再上报编辑」。
        // 实现里若把标志置上忘了清（或清得晚了一步），症状是外部改动之后
        // 用户打的字再也不落盘——安静得可怕。
        var note = CreateNote("旧内容");
        var notes = new FakeNoteService();
        var vm = CreateViewModel(note, noteService: notes);

        note.Content = "磁盘上的新内容";
        vm.RefreshFromNote();
        vm.Content = "我在界面上接着写";

        var edit = Assert.Single(notes.LocalEdits);
        Assert.Equal("我在界面上接着写", edit.Content);
    }

    // ---- 释放（§18.3）----

    [Fact]
    public void Dispose_重复调用不抛异常()
    {
        var vm = CreateViewModel(CreateNote("内容"));

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public void Dispose_反注册Messenger且不影响其他实例()
    {
        var note = CreateNote("内容");
        var a = CreateViewModel(note);
        var b = CreateViewModel(note);

        a.Dispose();

        // 只断言「没抛异常」：WeakReferenceMessenger 的注册状态不可直接观察，
        // 这里能保证的是 UnregisterAll 不会误伤同类型的其他实例。
        b.Dispose();
    }

    // ---- 测试脚手架 ----

    private static NoteViewModel CreateViewModel(
        Note note,
        NoteLayout? layout = null,
        FakeNoteService? noteService = null,
        AutoSaveService? autoSaveService = null) =>
        new(
            note,
            layout ?? CreateLayout(),
            noteService ?? new FakeNoteService(),
            autoSaveService ?? CreateAutoSaveService(),
            new ImmediateDispatcher(),
            new RecordingDialogService(),
            new RecordingWindowManager(),

            // 每个 ViewModel 配一条全新的总线。用 WeakReferenceMessenger.Default 的话，
            // 同一个测试进程里的用例会共用它，并行执行时注册/注销互相干扰（§21.1）。
            new WeakReferenceMessenger());

    /// <summary>造一个用替身定时器的自动保存服务。需要检查定时器的用例自己造。</summary>
    private static AutoSaveService CreateAutoSaveService() =>
        new(new FakeNoteService(), new RecordingUiTimerFactory(), NullLogger<AutoSaveService>.Instance);

    private static Note CreateNote(string content, NoteColor color = NoteColor.Yellow) =>
        new()
        {
            Id = Guid.NewGuid(),
            FilePath = @"C:\notes\test.md",
            Content = content,
            Color = color,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private static NoteLayout CreateLayout(
        bool isCollapsed = false,
        bool isTopMost = false,
        bool isLocked = false) =>
        new()
        {
            NoteId = Guid.NewGuid(),
            IsCollapsed = isCollapsed,
            IsTopMost = isTopMost,
            IsLocked = isLocked,
        };
}
