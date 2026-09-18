using System.IO;
using LumiMemo.App.Abstractions;
using Microsoft.Win32;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IFolderPicker"/> 的生产实现，用 WPF 自带的 <see cref="OpenFolderDialog"/>。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OpenFolderDialog"/> 是 .NET 8 才进入 WPF 的类型，在此之前选文件夹要么走
/// WinForms 的 <c>FolderBrowserDialog</c>（意味着打开 <c>UseWindowsForms</c>，把一个
/// 完整的第二套 UI 框架拖进来），要么自己 P/Invoke <c>SHBrowseForFolder</c>。
/// 现在它就在 <c>PresentationFramework</c> 里，本工程因此能保持
/// <c>UseWindowsForms=false</c>（§2.2）。
/// </para>
/// <para>
/// GPS 位置那两处传 <c>null</c> 是刻意的：文件夹选择对话框没有理由知道用户在哪，
/// 而 Windows 默认会在第一个参数为 null 时用「最近使用的文件」作为起点。
/// </para>
/// </remarks>
public sealed class FolderPickerDialog : IFolderPicker
{
    /// <inheritdoc />
    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,

            // 允许多选会让返回值的形状变成「集合」而不是「单个路径」，
            // 而全程序只有「一个笔记目录」这一个概念。
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
