using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LumiMemo.WinUI.Controls;

/// <summary>把摘要切片渲染成带高亮的文本：命中段用便签自己的颜色作文字背景。</summary>
/// <remarks>
/// <para>
/// 用包装控件（<see cref="UserControl"/>）而不是继承 <see cref="TextBlock"/>——
/// WinUI 的 TextBlock 是密封类。高亮走 WinUI 的 <see cref="TextHighlighter"/>：
/// 它按字符区间给文字加背景，正是为这类场景设计的（WinUI 的 Run 没有 Background 属性）。
/// </para>
/// <para>
/// 控件就服务于列表项的两种用途：<see cref="IsTitle"/> 为真时是标题行
/// （单行、加粗、深色），否则是内容预览行（两行封顶、尾部省略、灰色）。
/// 不开放成通用富文本控件；命中段文字两处都用深色，浅色底上对比度才够。
/// </para>
/// </remarks>
public sealed class SnippetTextBlock : UserControl
{
    private static readonly SolidColorBrush MatchForeground =
        new(Color.FromArgb(255, 0x40, 0x37, 0x47));

    private static readonly SolidColorBrush TitleForeground =
        new(Color.FromArgb(255, 0x40, 0x37, 0x47));

    private static readonly SolidColorBrush PreviewForeground =
        new(Color.FromArgb(255, 0x75, 0x69, 0x7C));

    public static readonly DependencyProperty SnippetProperty = DependencyProperty.Register(
        nameof(Snippet),
        typeof(IReadOnlyList<SnippetSegment>),
        typeof(SnippetTextBlock),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty NoteColorProperty = DependencyProperty.Register(
        nameof(NoteColor),
        typeof(NoteColor),
        typeof(SnippetTextBlock),
        new PropertyMetadata(NoteColor.Yellow, OnVisualChanged));

    public static readonly DependencyProperty IsTitleProperty = DependencyProperty.Register(
        nameof(IsTitle),
        typeof(bool),
        typeof(SnippetTextBlock),
        new PropertyMetadata(false, OnStyleChanged));

    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public SnippetTextBlock()
    {
        Content = _text;
        ApplyStyle();
    }

    /// <summary>摘要切片（由 <c>SnippetBuilder</c> 构建）。</summary>
    public IReadOnlyList<SnippetSegment>? Snippet
    {
        get => (IReadOnlyList<SnippetSegment>?)GetValue(SnippetProperty);
        set => SetValue(SnippetProperty, value);
    }

    /// <summary>便签颜色——命中段的文字背景用它。</summary>
    public NoteColor NoteColor
    {
        get => (NoteColor)GetValue(NoteColorProperty);
        set => SetValue(NoteColorProperty, value);
    }

    /// <summary>标题行（单行加粗深色）还是内容预览行（两行灰色）。</summary>
    public bool IsTitle
    {
        get => (bool)GetValue(IsTitleProperty);
        set => SetValue(IsTitleProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SnippetTextBlock)d).Rebuild();

    private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SnippetTextBlock)d;
        control.ApplyStyle();
        control.Rebuild();
    }

    private void ApplyStyle()
    {
        if (IsTitle)
        {
            _text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            _text.FontSize = 15;
            _text.MaxLines = 1;
            _text.Foreground = TitleForeground;
        }
        else
        {
            _text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            _text.FontSize = 14;
            _text.MaxLines = 2;
            _text.Foreground = PreviewForeground;
        }
    }

    private void Rebuild()
    {
        _text.TextHighlighters.Clear();

        if (Snippet is null || Snippet.Count == 0)
        {
            _text.Text = string.Empty;

            return;
        }

        // 先把全部片段拼成整段文本，再把命中片段换算成字符区间交给 TextHighlighter。
        _text.Text = string.Concat(Snippet.Select(static segment => segment.Text));

        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(NoteColorBackgrounds.Of(NoteColor)),
            Foreground = MatchForeground,
        };

        var offset = 0;

        foreach (SnippetSegment segment in Snippet)
        {
            if (segment.IsMatch)
            {
                highlighter.Ranges.Add(new TextRange
                {
                    StartIndex = offset,
                    Length = segment.Text.Length,
                });
            }

            offset += segment.Text.Length;
        }

        if (highlighter.Ranges.Count > 0)
        {
            _text.TextHighlighters.Add(highlighter);
        }
    }
}

/// <summary>便签颜色 → 便签纸背景色（与旧版 WPF 的 Colors.xaml 取同一组色值）。</summary>
internal static class NoteColorBackgrounds
{
    public static Color Of(NoteColor color) => color switch
    {
        NoteColor.Pink => Color.FromArgb(255, 0xFB, 0xE0, 0xEA),
        NoteColor.Blue => Color.FromArgb(255, 0xDF, 0xED, 0xFB),
        NoteColor.Green => Color.FromArgb(255, 0xE0, 0xF3, 0xE0),
        NoteColor.Purple => Color.FromArgb(255, 0xED, 0xE3, 0xF9),
        NoteColor.Orange => Color.FromArgb(255, 0xFC, 0xE7, 0xD4),
        NoteColor.Gray => Color.FromArgb(255, 0xEF, 0xEF, 0xEF),
        _ => Color.FromArgb(255, 0xFD, 0xF3, 0xC4),   // Yellow 与兜底
    };
}
