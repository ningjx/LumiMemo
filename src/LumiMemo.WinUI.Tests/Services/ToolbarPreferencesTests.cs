using LumiMemo.Core.Models;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.UI;
using Xunit;

namespace LumiMemo.WinUI.Tests.Services;

/// <summary>
/// <see cref="ToolbarPreferences"/>：文字底色与常用标题级别是<strong>全局</strong>值——
/// 改了要落盘，别的便签窗口要能收到通知。
/// </summary>
/// <remarks>
/// 「落盘」在替身里是同步完成的（<see cref="FakeSettingsStore"/> 立即返回），
/// 所以这里可以直接断言写盘次数。
/// </remarks>
public sealed class ToolbarPreferencesTests
{
    private static readonly Color YellowPaper = Color.FromArgb(255, 0xFD, 0xF3, 0xC4);

    private static readonly Color Blue = Color.FromArgb(255, 0x59, 0x8C, 0xD6);

    [Fact]
    public void 没设置过时给默认值()
    {
        var (prefs, _) = Create();

        // 兜底与合并前一致：便签黄纸色 + H2。
        Assert.Equal(YellowPaper, prefs.HighlightColor);
        Assert.Equal(2, prefs.HeadingLevel);
    }

    [Fact]
    public void 改底色会落盘并通知()
    {
        var (prefs, store) = Create();
        int raised = 0;
        prefs.HighlightColorChanged += (_, _) => raised++;

        prefs.SetHighlightColor(Blue);

        Assert.Equal(1, raised);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal("#FF598CD6", store.Settings.TextHighlightColor);
        Assert.Equal(Blue, prefs.HighlightColor);
    }

    [Fact]
    public void 同一个底色重复设置不再写盘()
    {
        var (prefs, store) = Create();

        prefs.SetHighlightColor(Blue);
        prefs.SetHighlightColor(Blue);

        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void 认不出来的颜色回退默认值()
    {
        // 文件是给用户手改的（§8.4）：改坏了不该让工具栏变透明或崩掉。
        var prefs = Create(new AppSettings { TextHighlightColor = "海的颜色" }).Preferences;

        Assert.Equal(YellowPaper, prefs.HighlightColor);
    }

    [Fact]
    public void 六位十六进制也认()
    {
        var prefs = Create(new AppSettings { TextHighlightColor = "#598CD6" }).Preferences;

        Assert.Equal(Blue, prefs.HighlightColor);
    }

    [Fact]
    public void 改常用标题级别会落盘并通知()
    {
        var (prefs, store) = Create();
        int raised = 0;
        prefs.HeadingLevelChanged += (_, _) => raised++;

        prefs.SetHeadingLevel(3);

        Assert.Equal(1, raised);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(3, store.Settings.DefaultHeadingLevel);
        Assert.Equal(3, prefs.HeadingLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void 常用标题只接受一到三级(int level)
    {
        var (prefs, store) = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => prefs.SetHeadingLevel(level));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void 设置里级别越界时回退H2()
    {
        // 手改出来的 9 不该让按钮显示一个不存在的级别（写盘时会被钳制，读的时候也要兜住）。
        var prefs = Create(new AppSettings { DefaultHeadingLevel = 9 }).Preferences;

        Assert.Equal(2, prefs.HeadingLevel);
    }

    private static (ToolbarPreferences Preferences, FakeSettingsStore Store) Create(AppSettings? settings = null)
    {
        var store = new FakeSettingsStore();
        var prefs = new ToolbarPreferences(
            settings ?? new AppSettings(), store, NullLogger<ToolbarPreferences>.Instance);

        return (prefs, store);
    }
}
