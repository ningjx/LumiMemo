using LumiText.Core.Editing;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 光标可见性规则单测（编辑器失焦收光标 + 关窗后不许再重绘）。
/// </summary>
public sealed class CaretVisibilityTests
{
    [Fact]
    public void Default_Blinking()
    {
        var visibility = new CaretVisibility();
        Assert.True(visibility.ShouldBlink);
    }

    [Fact]
    public void WindowDeactivated_Hides()
    {
        var visibility = new CaretVisibility();
        Assert.Equal(CaretVisibilityChange.Hidden, visibility.Update(editorFocused: true, windowActive: false));
        Assert.False(visibility.ShouldBlink);
    }

    [Fact]
    public void EditorFocusLost_Hides()
    {
        var visibility = new CaretVisibility();
        Assert.Equal(CaretVisibilityChange.Hidden, visibility.Update(editorFocused: false, windowActive: true));
        Assert.False(visibility.ShouldBlink);
    }

    [Fact]
    public void BackToTheEditor_ShowsAgain()
    {
        var visibility = new CaretVisibility();
        visibility.Update(editorFocused: true, windowActive: false);
        Assert.Equal(CaretVisibilityChange.Shown, visibility.Update(editorFocused: true, windowActive: true));
        Assert.True(visibility.ShouldBlink);
    }

    [Fact]
    public void SameStateAgain_ReportsNothing()
    {
        var visibility = new CaretVisibility();
        Assert.Equal(CaretVisibilityChange.None, visibility.Update(editorFocused: true, windowActive: true));

        visibility.Update(editorFocused: false, windowActive: false);
        // 已经收起了，再收一次仍是无变化
        Assert.Equal(CaretVisibilityChange.None, visibility.Update(editorFocused: false, windowActive: false));
    }

    /// <summary>
    /// 回归：关窗时 WinUI 照样会把"窗口失活 / 焦点走了"送进已经拆掉的编辑器
    /// （Unloaded → surface 已 Dispose）。那时必须什么都不做——否则宿主会去重绘已释放的
    /// Composition 对象：实机表现就是"连续开两个便签、关掉其中一个"必崩。
    /// </summary>
    [Fact]
    public void AfterDisposed_FocusChange_ReportsNothing()
    {
        var visibility = new CaretVisibility();
        visibility.MarkDisposed();

        Assert.Equal(CaretVisibilityChange.None,
            visibility.Update(editorFocused: false, windowActive: false));
        Assert.False(visibility.ShouldBlink);
    }
}
