using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using Xunit;

namespace LumiMemo.App.Tests.Views;

/// <summary>
/// <see cref="SettingsWindow"/> 的 XAML 能否加载。
/// </summary>
/// <remarks>
/// <para>
/// <strong>这不是「设置窗口能不能用」的测试</strong>，只是「XAML 合不合法」的测试。
/// 与 <c>ManagerWindowTests</c> 同一条理由：这类错误全在 <c>InitializeComponent()</c> 里炸，
/// 而它只在用户点齿轮时才被调用——也就是说，XAML 写坏了，程序会一直好好的，
/// 直到有人点开设置才崩。
/// </para>
/// <para>
/// 回收站那一页里 <c>Run Text="{Binding …}"</c> 拼出来的副标题、<c>DataTrigger</c> 控制的
/// 空列表提示与「已不在回收站里」角标，全是这类笔误的高发区。
/// <strong>不覆盖</strong>的是绑定求值是否正确、布局好不好看——那些只能肉眼验收。
/// </para>
/// <para>
/// <strong><see cref="SettingsWindowLauncher"/> 没有自动化测试</strong>，与
/// <c>WindowManager</c>、<c>TrayService</c> 同一档：它的三段逻辑（没有就新建、
/// 开着就唤到前面、关掉之后忘掉它）全都要真的 <c>Show()</c> 一个窗口才验得到，
/// 而在测试进程里弹出真窗口是不可接受的。它进手工清单。
/// </para>
/// </remarks>
public sealed class SettingsWindowTests
{
    [Fact]
    public void 构造_能加载XAML且不抛异常()
    {
        Exception? failure = StaThread.Run(() =>
        {
            // 不 Show()：那样才需要消息泵，而构造过程已经跑完了 InitializeComponent。
            var window = new SettingsWindow(NewViewModel());

            Assert.NotNull(window.Content);
            Assert.IsType<SettingsViewModel>(window.DataContext);
        });

        Assert.Null(failure);
    }

    [Fact]
    public void 关闭命令_把窗口关掉()
    {
        Exception? failure = StaThread.Run(() =>
        {
            var window = new SettingsWindow(NewViewModel());

            // 从未 Show() 过的窗口可以直接 Close()，不需要消息泵。
            // 这条路正是「点关闭按钮」走的那条：ViewModel 发事件，窗口自己 Close。
            ((SettingsViewModel)window.DataContext).CloseCommand.Execute(null);

            Assert.False(window.IsVisible);
        });

        Assert.Null(failure);
    }

    private static SettingsViewModel NewViewModel()
    {
        var paths = new AppPaths(@"C:\fake-local");
        paths.SetNotesFolder(@"D:\notes");

        var trash = new TrashService(
            new FakeTrashStore(), paths, new NoteStore(), new SearchIndex(), new FakeNoteRepository());

        return new SettingsViewModel(
            new FakeSettingsStore(),
            paths,
            trash,
            new RecordingSettingsApplier(),
            new RecordingDialogService(),
            new RecordingShellLauncher(),
            new ImmediateDispatcher());
    }
}
