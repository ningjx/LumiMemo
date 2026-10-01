using LumiMemo.WinUI.ViewModels;
using Xunit;

namespace LumiMemo.WinUI.Tests;

/// <summary>宿主冒烟：确认测试进程能加载 LumiMemo.WinUI 程序集里的非 UI 类型。</summary>
public sealed class HostSmokeTests
{
    [Fact]
    public void 能加载WinUI程序集的非UI类型() =>
        Assert.Equal("LumiMemo.WinUI.ViewModels", typeof(NoteViewModel).Namespace);
}
