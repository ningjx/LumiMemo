using System.Globalization;
using LumiMemo.App.Views.Converters;
using LumiMemo.Core.Models;
using Xunit;

namespace LumiMemo.App.Tests.Views;

/// <summary>
/// <see cref="NoteColorMatchesConverter"/> 的单元测试。
/// </summary>
/// <remarks>
/// 它可以直接单测：<c>IValueConverter</c> 是纯函数式的接口，
/// <c>Convert</c> 不碰任何 WPF 类型（真正的绑定求值才是无头测不了的那一半）。
/// </remarks>
public sealed class NoteColorMatchesConverterTests
{
    private static readonly NoteColorMatchesConverter Converter = new();

    private static object Matches(object? value, object? parameter) =>
        Converter.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture);

    [Fact]
    public void 颜色与参数相符时点亮()
    {
        Assert.True((bool)Matches(NoteColor.Blue, "Blue"));
    }

    [Fact]
    public void 颜色与参数不符时不亮()
    {
        Assert.False((bool)Matches(NoteColor.Blue, "Pink"));
    }

    [Fact]
    public void 参数大小写不敏感()
    {
        // XAML 里写的是 "Blue"，但 x:Static 那几处若有人改用小写，
        // 不该表现为「那一项永远不亮」这种毫无线索的症状。
        Assert.True((bool)Matches(NoteColor.Blue, "blue"));
    }

    [Fact]
    public void 枚举名拼错时不亮而不是抛异常()
    {
        // 拼错的后果只该是那一项不亮（XAML 里那一行就在眼前，看得见），
        // 而不是每次打开菜单都炸一次绑定错误。
        Assert.False((bool)Matches(NoteColor.Blue, "Bule"));
    }

    [Fact]
    public void 没有选中行时七项都不亮()
    {
        // 没有选中行时绑定交过来的是 null。
        Assert.False((bool)Matches(null, "Blue"));
    }

    [Fact]
    public void 参数不是字符串时不亮()
    {
        // ConverterParameter 只有从 XAML 过来才是字符串。走到这里说明
        // 有人把它当命令参数那样传了对象进来——不亮总好过抛异常。
        Assert.False((bool)Matches(NoteColor.Blue, 3));
    }

    [Fact]
    public void 反向转换抛异常()
    {
        // 反向那条路会写到只读的 NoteListItem.Color 上，本来就不该存在。
        // 真有人接了这条线，这里立刻炸掉比留一条静默通路强。
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(true, typeof(NoteColor), "Blue", CultureInfo.InvariantCulture));
    }
}
