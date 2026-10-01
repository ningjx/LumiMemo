using LumiMemo.Core.Services;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary><see cref="TodoMarkers"/> 的单元测试：待办前缀的识别、增删、切换与 Enter 决策。</summary>
public sealed class TodoMarkersTests
{
    // ---- 识别 ----

    [Fact]
    public void 未完成前缀_识别为待办行() => Assert.True(TodoMarkers.IsTodoLine("☐ 买牛奶"));

    [Fact]
    public void 已完成前缀_识别为待办行() => Assert.True(TodoMarkers.IsTodoLine("☑ 买牛奶"));

    [Fact]
    public void 普通行_不是待办行() => Assert.False(TodoMarkers.IsTodoLine("买牛奶"));

    [Fact]
    public void 符号在行中_不误判() => Assert.False(TodoMarkers.IsTodoLine("买 ☐ 牛奶"));

    [Fact]
    public void 只有符号没有空格_不误判() => Assert.False(TodoMarkers.IsTodoLine("☐买牛奶"));

    [Fact]
    public void 已完成行_IsCheckedLine为真()
    {
        Assert.True(TodoMarkers.IsCheckedLine("☑ 买牛奶"));
        Assert.False(TodoMarkers.IsCheckedLine("☐ 买牛奶"));
        Assert.False(TodoMarkers.IsCheckedLine("买牛奶"));
    }

    // ---- 加前缀 / 剥前缀 ----

    [Fact]
    public void 加前缀_普通行变未完成待办() =>
        Assert.Equal("☐ 买牛奶", TodoMarkers.WithPrefix("买牛奶"));

    [Fact]
    public void 加前缀_已是待办行原样返回() =>
        Assert.Equal("☑ 买牛奶", TodoMarkers.WithPrefix("☑ 买牛奶"));

    [Fact]
    public void 剥前缀_待办行去掉符号() =>
        Assert.Equal("买牛奶", TodoMarkers.WithoutPrefix("☐ 买牛奶"));

    [Fact]
    public void 剥前缀_非待办行原样返回() =>
        Assert.Equal("买牛奶", TodoMarkers.WithoutPrefix("买牛奶"));

    [Fact]
    public void 剥前缀_空内容行返回空串() =>
        Assert.Equal(string.Empty, TodoMarkers.WithoutPrefix("☐ "));

    // ---- 切换符号 ----

    [Fact]
    public void 切换_未完成变已完成() =>
        Assert.Equal("☑ 买牛奶", TodoMarkers.ToggleSymbol("☐ 买牛奶"));

    [Fact]
    public void 切换_已完成变未完成() =>
        Assert.Equal("☐ 买牛奶", TodoMarkers.ToggleSymbol("☑ 买牛奶"));

    [Fact]
    public void 切换_非待办行原样返回() =>
        Assert.Equal("买牛奶", TodoMarkers.ToggleSymbol("买牛奶"));

    [Fact]
    public void 切换_只动行首符号不动行中内容() =>
        Assert.Equal("☑ a ☐ b", TodoMarkers.ToggleSymbol("☐ a ☐ b"));

    // ---- Enter 决策 ----

    [Fact]
    public void Enter_非待办行_不拦截() =>
        Assert.Equal(TodoEnterAction.None, TodoMarkers.EnterAction("买牛奶"));

    [Fact]
    public void Enter_有内容的待办行_续行() =>
        Assert.Equal(TodoEnterAction.Continue, TodoMarkers.EnterAction("☐ 买牛奶"));

    [Fact]
    public void Enter_已完成有内容的行_续行() =>
        Assert.Equal(TodoEnterAction.Continue, TodoMarkers.EnterAction("☑ 买牛奶"));

    [Fact]
    public void Enter_空待办行_退出() =>
        Assert.Equal(TodoEnterAction.Exit, TodoMarkers.EnterAction("☐ "));

    [Fact]
    public void Enter_只有空白的待办行_退出() =>
        Assert.Equal(TodoEnterAction.Exit, TodoMarkers.EnterAction("☑   "));
}
