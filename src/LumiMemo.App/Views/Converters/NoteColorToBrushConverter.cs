using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Views.Converters;

/// <summary>
/// 把 <see cref="NoteColor"/> 换成调色板里那支强调色笔刷（§15.3）。管理器列表的颜色点用它。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么用转换器而不是在 <c>NoteListItem</c> 上放一个 <c>Brush</c> 属性。</strong>
/// 笔刷是界面概念，而 <c>NoteListItem</c> 是便签的投影——把笔刷放上去，等于让
/// "哪种颜色长什么样"这件事从 <c>Resources/Colors.xaml</c> 漏出去，多出第二个真相源。
/// 转换器按名字去资源字典里取，调色板始终只有一处。
/// </para>
/// <para>
/// 键名由枚举成员名拼出来（<c>Note</c> + 颜色名 + <c>AccentBrush</c>），
/// 因此调色板里漏掉一个颜色时不会编译报错、只会查不到——那种情况下退回一支中性灰，
/// 列表照样能用，而颜色点的大小与位置不变，一眼看得出是哪一行的颜色没了。
/// 不要改成抛异常：一个漏掉的色值不该把整个管理器的列表掀掉。
/// </para>
/// <para>
/// 这里取<strong>强调色</strong>而不是背景色：颜色点只有 8 像素见方，
/// 七个浅色背景在白底上彼此分不开，而强调色是各自饱和的那一支。
/// </para>
/// </remarks>
public sealed class NoteColorToBrushConverter : IValueConverter
{
    /// <summary>调色板里查不到时用的那支灰。</summary>
    private static readonly SolidColorBrush Fallback = CreateFallback();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NoteColor color)
        {
            return Fallback;
        }

        string key = $"Note{color}AccentBrush";

        // Application.Current 为 null 只会出现在没有 WPF 应用的进程里（单元测试）。
        // 那种情况下也走灰的兜底，而不是让一次 NullReferenceException 把绑定打崩。
        return Application.Current?.TryFindResource(key) as Brush ?? Fallback;
    }

    /// <summary>不需要反向转换：颜色点是单向展示，改颜色走的是右键菜单那条路。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("颜色点只读。改便签颜色走右键菜单的「颜色」，不是往界面里写。");

    private static SolidColorBrush CreateFallback()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));

        // 冻结：这支笔刷要跨绑定共享，可变的话哪天有人改了它的 Color 会波及所有用到它的行。
        brush.Freeze();

        return brush;
    }
}
