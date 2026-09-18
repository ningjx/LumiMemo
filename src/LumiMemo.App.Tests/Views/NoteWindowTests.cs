using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.App.Views;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.App.Tests.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的 XAML 能否加载。
/// </summary>
/// <remarks>
/// <para>
/// <strong>这不是「窗口能不能用」的测试</strong>，只是「XAML 合不合法」的测试。
/// 它能抓住的是一整类会直接崩进程的错：标签名打错、属性名不存在、
/// <c>StaticResource</c> 指向一个不存在的键、<c>ControlTemplate</c> 里写了
/// 目标类型没有的触发器属性（本仓库真撞过一次：<c>MC4109</c>，
/// 在 <c>TargetType="ButtonBase"</c> 的模板里写 <c>IsChecked</c>）。
/// </para>
/// <para>
/// <strong>为什么值得单独测</strong>：这些错误全在 <c>InitializeComponent()</c> 里炸，
/// 而它默认只在「用户双击一条便签」时才被调用——也就是说，XAML 写坏了，
/// 程序照样能启动、管理器窗口照样能开、列表照样能列出来，直到用户点了才有事。
/// 在没有这层测试的情况下，「构建通过 + 测试全绿 + 程序正常运行」
/// 与「<c>NoteWindow.xaml</c> 里有一处笔误」，是完全兼容的两种状态。
/// </para>
/// <para>
/// <strong>不覆盖的东西</strong>：绑定表达式是否求值正确（写错只会静默输出到调试通道）、
/// <c>WindowChrome</c> 的实际效果、尺寸与位置、以及所有交互。
/// 那些只能靠肉眼验收。见 <see cref="StaThread"/> 的注释。
/// </para>
/// </remarks>
public sealed class NoteWindowTests
{
    [Fact]
    public void 构造_能加载XAML且不抛异常()
    {
        Exception? failure = StaThread.Run(() =>
        {
            // 不 Show()，也不 Close()：Show 之后才需要消息泵，而从未显示过的窗口
            // 没有 HWND 要释放，构造完放着即可。构造过程已经跑完了 InitializeComponent，
            // 要验的东西全在里面。
            var window = new NoteWindow(CreateViewModel(), CreateAutoSaveService());

            Assert.NotNull(window.Content);
            Assert.Equal("便签", window.Title);
        });

        Assert.Null(failure);
    }

    [Fact]
    public void 构造_参数为null时抛ArgumentNullException()
    {
        Exception? failure = StaThread.Run(() =>
        {
            // 便签窗口是「一个 ViewModel 对一个窗口」的强绑定。
            // 放 null 进去只会在后面某次绑定时才炸，不如在构造处立刻拦住。
            Assert.Throws<ArgumentNullException>(() => new NoteWindow(null!, CreateAutoSaveService()));
            Assert.Throws<ArgumentNullException>(() => new NoteWindow(CreateViewModel(), null!));
        });

        Assert.Null(failure);
    }

    [Fact]
    public void 构造_置顶状态从ViewModel落到窗口()
    {
        Exception? failure = StaThread.Run(() =>
        {
            var window = new NoteWindow(CreateViewModel(isTopMost: true), CreateAutoSaveService());

            // WindowManager 之后会订阅 PropertyChanged 同步后续变化，
            // 但「打开那一刻」的状态是构造函数负责的，那一下没有事件可听。
            Assert.True(window.Topmost);
            Assert.True(window.ViewModel.IsTopMost);
            Assert.Equal(window.ViewModel.Id, window.NoteId);
        });

        Assert.Null(failure);
    }

    [Fact]
    public void 构造_DataContext就是传入的ViewModel()
    {
        Exception? failure = StaThread.Run(() =>
        {
            NoteViewModel viewModel = CreateViewModel();

            var window = new NoteWindow(viewModel, CreateAutoSaveService());

            // 整份 XAML 的绑定都挂在 DataContext 上，它没设对的话
            // 窗口会正常打开但一片空白——那是肉眼最难判断归属的一类故障。
            Assert.Same(viewModel, window.DataContext);
        });

        Assert.Null(failure);
    }

    private static NoteViewModel CreateViewModel(bool isTopMost = false) =>
        new(
            new Note
            {
                Id = Guid.NewGuid(),
                FilePath = @"C:\notes\test.md",
                Content = "# 购物清单\r\n- 牛奶",
                Color = NoteColor.Yellow,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new NoteLayout { NoteId = Guid.NewGuid(), IsTopMost = isTopMost },
            new FakeNoteService(),
            CreateAutoSaveService(),
            new ImmediateDispatcher(),
            new RecordingDialogService(),
            new RecordingWindowManager(),
            new WeakReferenceMessenger());

    private static AutoSaveService CreateAutoSaveService() =>
        new(new FakeNoteService(), new RecordingUiTimerFactory(), NullLogger<AutoSaveService>.Instance);
}
