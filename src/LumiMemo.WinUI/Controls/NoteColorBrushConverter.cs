using LumiMemo.Core.Models;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LumiMemo.WinUI.Controls;

/// <summary>便签颜色 → 深色点缀画刷（列表项左缘色条用）。</summary>
/// <remarks>
/// 画刷必须在 XAML 运行时里创建；放在转换器里而不是 ViewModel 侧，
/// 单元测试构造列表项时就不会碰到 UI 对象（SolidColorBrush 在测试宿主里会直接抛）。
/// </remarks>
public sealed class NoteColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(NoteColorPalette.Accent((NoteColor)value));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
