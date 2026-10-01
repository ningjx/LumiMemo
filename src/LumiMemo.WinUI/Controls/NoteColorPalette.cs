using LumiMemo.Core.Models;
using Windows.UI;

namespace LumiMemo.WinUI.Controls;

/// <summary>便签颜色的画笔映射：淡色"纸面"用于高亮底，深色"点缀"用于色条与色板。</summary>
internal static class NoteColorPalette
{
    /// <summary>便签纸背景色（与旧版 WPF 的 Colors.xaml 取同一组色值）。</summary>
    public static Color Paper(NoteColor color) => color switch
    {
        NoteColor.Pink => Color.FromArgb(255, 0xFB, 0xE0, 0xEA),
        NoteColor.Blue => Color.FromArgb(255, 0xDF, 0xED, 0xFB),
        NoteColor.Green => Color.FromArgb(255, 0xE0, 0xF3, 0xE0),
        NoteColor.Purple => Color.FromArgb(255, 0xED, 0xE3, 0xF9),
        NoteColor.Orange => Color.FromArgb(255, 0xFC, 0xE7, 0xD4),
        NoteColor.Gray => Color.FromArgb(255, 0xEF, 0xEF, 0xEF),
        _ => Color.FromArgb(255, 0xFD, 0xF3, 0xC4),   // Yellow 与兜底
    };

    /// <summary>深色点缀（列表项左缘色条、改色色板）；与筛选色块取同一组色值。</summary>
    public static Color Accent(NoteColor color) => color switch
    {
        NoteColor.Pink => Color.FromArgb(255, 0xB0, 0x35, 0x6B),
        NoteColor.Blue => Color.FromArgb(255, 0x1F, 0x5F, 0xA8),
        NoteColor.Green => Color.FromArgb(255, 0x2E, 0x7D, 0x46),
        NoteColor.Purple => Color.FromArgb(255, 0x6A, 0x3F, 0xA0),
        NoteColor.Orange => Color.FromArgb(255, 0xB7, 0x5E, 0x12),
        NoteColor.Gray => Color.FromArgb(255, 0x5F, 0x5F, 0x5F),
        _ => Color.FromArgb(255, 0xB5, 0x89, 0x00),   // Yellow 与兜底
    };

    /// <summary>颜色的中文名（tooltip 用）。</summary>
    public static string DisplayName(NoteColor color) => color switch
    {
        NoteColor.Pink => "粉色",
        NoteColor.Blue => "蓝色",
        NoteColor.Green => "绿色",
        NoteColor.Purple => "紫色",
        NoteColor.Orange => "橙色",
        NoteColor.Gray => "灰色",
        _ => "黄色",
    };
}
