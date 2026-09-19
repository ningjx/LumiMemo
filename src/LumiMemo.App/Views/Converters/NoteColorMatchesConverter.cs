using System.Globalization;
using System.Windows.Data;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Views.Converters;

/// <summary>
/// 判断一个 <see cref="NoteColor"/> 是不是参数指定的那一个。右键菜单「颜色」子菜单里
/// 那七项靠它点亮当前颜色。
/// </summary>
/// <remarks>
/// <para>
/// 七个子菜单项是<strong>七个静态的 XAML 节点</strong>（颜色是编译期就定下来的，
/// 没有第二个来源会往这个菜单里加颜色），所以每一项只能写死一个
/// <c>ConverterParameter="Blue"</c> 这样的字符串去跟当前值比。
/// 枚举名与字符串的对应关系由 <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> 折算，
/// 拼错的后果是那一项永远不亮——不会崩，也不会让别的项跟着错。
/// </para>
/// <para>
/// <strong>只用于 <c>MenuItem.IsChecked</c> 这一处，且必须 <c>Mode=OneWay</c>。</strong>
/// <c>IsChecked</c> 的默认绑定模式是 <c>TwoWay</c>，且它在默认模板上真的会被用户点击改掉——
/// 反向那条路会写到 <c>NoteListItem.Color</c> 上，而那个属性是只读的（<c>=> Note.Color</c>）。
/// 那条路本来就不该存在：改颜色的动作是「点了哪一项」的信息，走 <c>Command</c> 传到
/// ViewModel，而不是靠 WPF 替我们把复选框拨过去。所以这里干脆不实现反向，
/// 让写错的那天立刻炸掉，而不是留下一条谁也说不清的静默通路。
/// </para>
/// </remarks>
public sealed class NoteColorMatchesConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NoteColor color || parameter is not string name)
        {
            // 没有选中行时绑定给的是 null，此时七项都不该亮。
            return false;
        }

        return Enum.TryParse(name, ignoreCase: true, out NoteColor expected) && expected == color;
    }

    /// <summary>见类注释：反向这条路刻意不存在。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("颜色子菜单是只读的选中状态，改颜色走 SetColorCommand。");
}
