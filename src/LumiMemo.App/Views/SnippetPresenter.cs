using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LumiMemo.Core.Search;

namespace LumiMemo.App.Views;

/// <summary>
/// 把一个 <see cref="SnippetSegment"/> 列表画成一行文字，命中段换色加粗（§12.3、§15.8）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么必须是个派生控件。</strong>本该写成一个 <c>Style</c> 或者干脆在 XAML 里用
/// <c>&lt;ItemsControl&gt;</c>，但摘要要显示的是<em>一行连着排的文字</em>：用 <c>TextBlock</c> 的话
/// 各段的排布、换行与省略号由 WPF 的文本排版引擎一次算完，而 <c>ItemsControl</c> 会把每一段
/// 变成独立的排版单元，段与段之间会出现多出来的间距，行尾省略号也失去作用。
/// 而 <c>TextBlock.Inlines</c> <strong>不是依赖属性</strong>，绑不上——于是只剩派生一个类、
/// 在属性变更时自己重建 <c>Inlines</c> 这一条路。
/// </para>
/// <para>
/// <strong>只有命中段设前景色。</strong>其余段一律不设，靠 <see cref="TextElement.Foreground"/>
/// 的继承从外层 <c>TextBlock</c> 拿到颜色。这样调用方在 XAML 上写 <c>Foreground="…"</c> 就
/// 统一改掉了非命中部分的颜色，而本控件不必替界面决定"普通文字该是什么颜色"——
/// 那属于视图层，不属于这里。
/// </para>
/// </remarks>
public sealed class SnippetPresenter : TextBlock
{
    /// <summary>要渲染的片段。</summary>
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IReadOnlyList<SnippetSegment>),
            typeof(SnippetPresenter),
            new PropertyMetadata(null, OnSegmentsChanged));

    /// <summary>命中段的前景色，取自 §15.3 的 Accent。</summary>
    /// <remarks>
    /// 冻结之后可以跨线程共享，也省掉每次重建 <c>Inlines</c> 时的变更通知开销。
    /// </remarks>
    private static readonly SolidColorBrush MatchBrush = CreateMatchBrush();

    /// <summary>要渲染的片段。为 <c>null</c> 或空列表时本控件不显示任何文字。</summary>
    public IReadOnlyList<SnippetSegment>? Segments
    {
        get => (IReadOnlyList<SnippetSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SnippetPresenter)d).RebuildInlines((IReadOnlyList<SnippetSegment>?)e.NewValue);

    /// <summary>整体重建 <c>Inlines</c>。</summary>
    /// <remarks>
    /// 不做「复用已有的 <c>Run</c>、只改文字」那种增量更新：片段数量与切分位置每次都可能完全不同，
    /// 增量更新要先对齐两边的段再决定增删，代码量远超重建，而一行摘要只有几段。
    /// </remarks>
    private void RebuildInlines(IReadOnlyList<SnippetSegment>? segments)
    {
        Inlines.Clear();

        if (segments is null)
        {
            return;
        }

        foreach (SnippetSegment segment in segments)
        {
            var run = new Run(segment.Text);

            if (segment.IsMatch)
            {
                run.Foreground = MatchBrush;
                run.FontWeight = FontWeights.SemiBold;
            }

            Inlines.Add(run);
        }
    }

    private static SolidColorBrush CreateMatchBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xB5, 0x89, 0x00));
        brush.Freeze();

        return brush;
    }
}
