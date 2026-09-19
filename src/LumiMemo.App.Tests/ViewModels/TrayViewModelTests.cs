using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.Tests.Views;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using Xunit;

namespace LumiMemo.App.Tests.ViewModels;

/// <summary>
/// <see cref="TrayViewModel"/> 的菜单动作（§15.9）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>菜单长什么样、图标怎么画不在这里</strong>——那些在 <c>TrayService</c> 里，
/// 要真实的消息循环，只能手工验。这里覆盖的是<strong>每一项点下去之后发生了什么</strong>，
/// 而那恰恰是菜单里唯一会出错的部分：转发漏了一条路径、计数没刷新、
/// 或者「退出」忘了接上。
/// </para>
/// <para>
/// 少数几个用例必须跑在 STA 线程上（凡是要造出真窗口的），因此套着 <see cref="StaThread"/>。
/// 其余一律留在测试线程上——多绕一层只会让失败信息更难读。
/// </para>
/// </remarks>
public sealed class TrayViewModelTests
{
    // ---- 单击行为的分派（§8.2 的 singleClickTrayAction）----

    [Fact]
    public void 单击_缺省是把管理器弄到前面()
    {
        using var h = new TrayHarness();

        h.Vm.SingleClickCommand.Execute(null);

        Assert.Equal(1, h.Presenter.BringToFrontCount);
        Assert.Empty(h.Manager.NoteService.CreatedNotes);
        Assert.Empty(h.Manager.Windows.Calls);
    }

    [Fact]
    public void 单击_设成newNote时新建一张便签()
    {
        using var h = new TrayHarness();
        h.Vm.SingleClickAction = "newNote";

        h.Vm.SingleClickCommand.Execute(null);

        Assert.Single(h.Manager.NoteService.CreatedNotes);
        Assert.Equal(0, h.Presenter.BringToFrontCount);
    }

    [Fact]
    public void 单击_设成showAllNotes时显示全部()
    {
        using var h = new TrayHarness();
        h.Vm.SingleClickAction = TrayViewModel.ShowAllNotesAction;

        h.Vm.SingleClickCommand.Execute(null);

        Assert.Contains("ShowAllNotes()", h.Manager.Windows.Calls);
        Assert.Equal(0, h.Presenter.BringToFrontCount);
    }

    [Fact]
    public void 单击_取值不认识时按缺省处理()
    {
        // 配置文件允许被手改（§9.3），写错一个词不该让图标点了没反应。
        using var h = new TrayHarness();
        h.Vm.SingleClickAction = "让便签跳个舞";

        h.Vm.SingleClickCommand.Execute(null);

        Assert.Equal(1, h.Presenter.BringToFrontCount);
    }

    [Fact]
    public void 双击_固定显示全部便签()
    {
        // 双击不受 singleClickTrayAction 影响（§15.9）。
        using var h = new TrayHarness();
        h.Vm.SingleClickAction = "newNote";

        h.Vm.DoubleClickCommand.Execute(null);

        Assert.Contains("ShowAllNotes()", h.Manager.Windows.Calls);
        Assert.Empty(h.Manager.NoteService.CreatedNotes);
    }

    // ---- 转发给管理器（§17.6：同一件事只有一条路径）----

    [Fact]
    public void 显示全部_该开的便签都开出来再统一亮一遍()
    {
        using var h = new TrayHarness();

        Note first = AddNote(h, "甲");
        Note second = AddNote(h, "乙");

        h.Vm.ShowAllNotesCommand.Execute(null);

        Assert.Equal(2, h.Manager.Windows.Calls.Count(c => c.StartsWith("ShowNote(", StringComparison.Ordinal)));
        // 顺序也不能反：先按 OpenAll 开窗，再统一亮出来。
        Assert.Equal("ShowAllNotes()", h.Manager.Windows.Calls[^1]);
        Assert.True(h.Manager.Windows.IsNoteOpen(first.Id));
        Assert.True(h.Manager.Windows.IsNoteOpen(second.Id));
    }

    [Fact]
    public void 收起全部_走窗口层而不动数据()
    {
        using var h = new TrayHarness();

        h.Vm.HideAllNotesCommand.Execute(null);

        Assert.Equal(["HideAllNotes()"], h.Manager.Windows.Calls);
        Assert.Empty(h.Manager.NoteService.SavedNoteIds);
    }

    [Fact]
    public void 重新加载_重新扫一遍笔记目录()
    {
        using var h = new TrayHarness();

        h.Vm.ReloadAllCommand.Execute(null);

        Assert.Equal(1, h.Manager.NoteService.LoadAllCallCount);
    }

    [Fact]
    public void 便签列表_把管理器弄到前面()
    {
        using var h = new TrayHarness();

        h.Vm.OpenManagerCommand.Execute(null);

        Assert.Equal(1, h.Presenter.BringToFrontCount);
    }

    // ---- 回收站计数 ----

    [Fact]
    public void 回收站标题_空的时候没有括号()
    {
        using var h = new TrayHarness();

        Assert.Equal("回收站...", h.Vm.TrashMenuHeader);
    }

    [Fact]
    public async Task 刷新回收站计数_数得出来时写进标题()
    {
        using var h = new TrayHarness();
        h.TrashStore.Add("甲.md");
        h.TrashStore.Add("乙.md");

        await h.Vm.RefreshTrashCountAsync();

        Assert.Equal(2, h.Vm.TrashCount);
        Assert.Equal("回收站（2）...", h.Vm.TrashMenuHeader);
    }

    [Fact]
    public async Task 刷新回收站计数_标题会跟着发通知()
    {
        // 菜单项的文字绑在 TrashMenuHeader 上，而它是从 TrashCount 算出来的。
        // 少了那条通知，菜单会一直显示上一次的条目数——而它看起来完全正常。
        using var h = new TrayHarness();
        h.TrashStore.Add("甲.md");

        List<string> changed = [];
        ((INotifyPropertyChanged)h.Vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        await h.Vm.RefreshTrashCountAsync();

        Assert.Contains(nameof(TrayViewModel.TrashCount), changed);
        Assert.Contains(nameof(TrayViewModel.TrashMenuHeader), changed);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(FormatException))]
    public async Task 刷新回收站计数_数不出来时归零且不抛(Type exceptionType)
    {
        // 菜单弹不出来比数字不准严重得多，所以这里必须吞。
        using var h = new TrayHarness();
        h.TrashStore.ListException = (Exception)Activator.CreateInstance(exceptionType)!;

        await h.Vm.RefreshTrashCountAsync();

        Assert.Equal(0, h.Vm.TrashCount);
    }

    [Fact]
    public async Task 刷新回收站计数_不认识的异常照样抛出去()
    {
        // 只吞「磁盘上的东西不对」这一类。别的一律放行——吞掉它们
        // 会把一个真正的 bug 变成「托盘上的数字一直是 0」。
        using var h = new TrayHarness();
        h.TrashStore.ListException = new InvalidOperationException("编程错误");

        await Assert.ThrowsAsync<InvalidOperationException>(h.Vm.RefreshTrashCountAsync);
    }

    // ---- 打开文件夹、关于、退出 ----

    [Fact]
    public async Task 打开笔记文件夹_交给外壳()
    {
        using var h = new TrayHarness();

        await h.Vm.OpenNotesFolderCommand.ExecuteAsync(null);

        Assert.Equal([@"D:\notes"], h.Shell.OpenedFolders);
        Assert.Empty(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public async Task 打开笔记文件夹_打不开时提示用户()
    {
        using var h = new TrayHarness();
        h.Shell.Result = false;

        await h.Vm.OpenNotesFolderCommand.ExecuteAsync(null);

        Assert.Single(h.Dialogs.ErrorRequests);
        Assert.Contains(@"D:\notes", h.Dialogs.ErrorRequests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 关于_报出版本号()
    {
        using var h = new TrayHarness();

        await h.Vm.AboutCommand.ExecuteAsync(null);

        string request = Assert.Single(h.Dialogs.InfoRequests);

        Assert.StartsWith("关于 LumiMemo|", request, StringComparison.Ordinal);

        // 版本号取三段（0.1.0），不是四段。
        Assert.Matches(@"LumiMemo \d+\.\d+\.\d+", request);
    }

    [Fact]
    public void 退出_请求退出而不是自己收尾()
    {
        // 收尾（停自动保存 → flush → 写 layout）挂在 App.OnExit 上，
        // 这里多发一次就多一条会与那条路走岔的分支。
        using var h = new TrayHarness();

        h.Vm.ExitCommand.Execute(null);

        Assert.Equal(1, h.Lifetime.ShutdownCount);
    }

    // ---- 设置窗口（要真窗口，跑在 STA 上）----

    [Fact]
    public void 打开回收站_落在回收站那一页()
    {
        Exception? failure = StaThread.Run(() =>
        {
            SettingsWindow? created = null;
            using var h = new TrayHarness(new SettingsWindowLauncher(() => created = NewSettingsWindow()));

            h.Vm.OpenTrashCommand.Execute(null);

            Assert.Equal((int)SettingsTab.Trash, SelectedTabIndex(created));
        });

        Assert.Null(failure);
    }

    [Fact]
    public void 打开设置_落在常规那一页()
    {
        Exception? failure = StaThread.Run(() =>
        {
            SettingsWindow? created = null;
            using var h = new TrayHarness(new SettingsWindowLauncher(() => created = NewSettingsWindow()));

            h.Vm.OpenSettingsCommand.Execute(null);

            Assert.Equal((int)SettingsTab.General, SelectedTabIndex(created));
        });

        Assert.Null(failure);
    }

    /// <summary>
    /// 读设置窗口当前选中的页签。
    /// </summary>
    /// <remarks>
    /// 走 <c>FindName</c> 而不是直接把 <c>Tabs</c> 暴露出来：那是个 <c>x:Name</c> 生成的
    /// 内部字段，外面本来就不该看得到它。而「页签序号就是 <see cref="SettingsTab"/> 的值」
    /// 这条约定正是这里要钉住的东西——它是那两个文件之间唯一的联系。
    /// </remarks>
    private static int SelectedTabIndex(SettingsWindow? window) =>
        ((TabControl?)window!.FindName("Tabs"))?.SelectedIndex ?? -1;

    /// <summary>
    /// 造一个设置窗口。与 <c>SettingsWindowTests</c> 里那份重复，是刻意的：
    /// 两个测试工程之间都不共享替身，同一个工程里为一份十行的装配再抽一层反而更难读。
    /// </summary>
    private static SettingsWindow NewSettingsWindow()
    {
        var paths = new AppPaths(@"C:\fake-local");
        paths.SetNotesFolder(@"D:\notes");

        var trash = new TrashService(
            new FakeTrashStore(), paths, new NoteStore(), new SearchIndex(), new FakeNoteRepository());

        return new SettingsWindow(new SettingsViewModel(
            new FakeSettingsStore(),
            paths,
            trash,
            new RecordingSettingsApplier(),
            new RecordingDialogService(),
            new RecordingShellLauncher(),
            new ImmediateDispatcher(),
            new WeakReferenceMessenger()));
    }

    /// <summary>加一张便签，并让它出现在 <c>OpenAll</c> 的结果里。</summary>
    private static Note AddNote(TrayHarness h, string content)
    {
        Note note = ManagerHarness.NewNote(content);

        h.Manager.Add(note);
        h.Manager.NoteService.OpenAllResult.Add(new NoteOpenRequest(
            note, h.Manager.LayoutStore.GetOrCreate(note.Id)));

        return note;
    }
}
