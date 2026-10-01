using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using Microsoft.UI.Xaml;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>便笺列表展示数据：生成状态与标题/内容的搜索摘要。</summary>
/// <remarks>
/// 标题与内容各自带一份摘要切片：命中可能只落在标题里（比如搜 AI 标题的词，
/// 正文里没有），只高亮内容会让用户觉得「搜到了标题词却什么反馈都没有」。
/// </remarks>
public sealed class NoteListItem(
    Note note,
    bool isGenerating,
    IReadOnlyList<SnippetSegment> titleSnippet,
    IReadOnlyList<SnippetSegment> snippet)
{
    public Note Note { get; } = note;

    public bool IsGenerating { get; } = isGenerating;

    public Visibility ProgressVisibility => IsGenerating ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>标题行的摘要切片（标题很短，一般就是整条标题加了高亮）。</summary>
    public IReadOnlyList<SnippetSegment> TitleSnippet { get; } = titleSnippet;

    /// <summary>内容预览：命中点附近的摘要切片（没有查询词时是开头预览）。</summary>
    public IReadOnlyList<SnippetSegment> Snippet { get; } = snippet;
}
