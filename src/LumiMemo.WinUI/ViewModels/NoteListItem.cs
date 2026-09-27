using LumiMemo.Core.Models;
using Microsoft.UI.Xaml;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>便笺列表展示数据，包含不写入便笺文件的生成状态。</summary>
public sealed class NoteListItem(Note note, bool isGenerating)
{
    public Note Note { get; } = note;
    public string Title => Note.Title;
    public string Content => Note.Content;
    public bool IsGenerating { get; } = isGenerating;
    public Visibility ProgressVisibility => IsGenerating ? Visibility.Visible : Visibility.Collapsed;
}
