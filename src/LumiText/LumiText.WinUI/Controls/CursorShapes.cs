using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace LumiText.WinUI.Controls;

/// <summary>给任意元素改光标的小助手（Phase 3 M4 新增：勾选悬停手型、缩放手柄光标）。</summary>
/// <remarks>
/// <c>ProtectedCursor</c> 是 protected 成员，XAML 与公开 API 都够不着；
/// 反射设置是 WinUI 3 里给元素改光标的惯用手法。失败只影响观感，不抛。
/// </remarks>
internal static class CursorShapes
{
    public static void SetShape(UIElement element, InputSystemCursorShape shape)
    {
        try
        {
            typeof(UIElement)
                .GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(element, InputSystemCursor.Create(shape));
        }
        catch (Exception)
        {
            // 光标只是观感，设不上就保持默认。
        }
    }
}
