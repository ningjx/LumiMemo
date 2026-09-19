using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.Views;
using Xunit;

namespace LumiMemo.App.Tests.Views;

/// <summary>
/// <see cref="ManagerWindow"/> 的 XAML 能否加载。
/// </summary>
/// <remarks>
/// <para>
/// <strong>这不是「窗口能不能用」的测试</strong>，只是「XAML 合不合法」的测试。
/// 与 <c>NoteWindowTests</c> 同一条理由：这类错误全在 <c>InitializeComponent()</c> 里炸，
/// 而它只在启动序列走到「开管理器」时才被调用——也就是说，XAML 写坏了，
/// 程序会一直跑到那个点才崩，而不是构建时就红。
/// </para>
/// <para>
/// 本轮新增的 <see cref="SnippetPresenter"/> 是自绘控件，它出现在模板里的那一行
/// （<c>Segments="{Binding SubtitleSegments}"</c>、<c>MaxWidth</c>、<c>TextTrimming</c>）
/// 一旦有笔误就是这类崩溃。<strong>不覆盖</strong>的是绑定求值是否正确、
/// 布局好不好看——那些只能肉眼验收。
/// </para>
/// </remarks>
public sealed class ManagerWindowTests
{
    [Fact]
    public void 构造_能加载XAML且不抛异常()
    {
        using var h = new ManagerHarness();

        Exception? failure = StaThread.Run(() =>
        {
            // 不 Show()：那样才需要消息泵，而构造过程已经跑完了 InitializeComponent。
            var window = new ManagerWindow(h.Vm);

            Assert.NotNull(window.Content);
            Assert.Same(h.Vm, window.DataContext);
        });

        Assert.Null(failure);
    }
}
