using LumiMemo.Core.Models;
using LumiMemo.Core.Search;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 管理器列表里的一行（§15.8）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>刻意做成不可变记录而不是 <c>ObservableObject</c></strong>：列表里的每一行都是
/// 从 <see cref="Note"/> 算出来的快照，任何一条数据变了都意味着列表需要重排
/// （排序键就是 <c>UpdatedAt</c>），而重排必然要重建整个集合。
/// 让每一行自己发通知只会得到「位置变了但顺序没变」这种自相矛盾的中间态。
/// </para>
/// <para>
/// 它是个纯投影，因此可以直接在测试里断言两个字符串，不需要窗口也不需要 WPF。
/// </para>
/// </remarks>
/// <param name="Note">被展示的便签。<strong>不复制内容</strong>——复制一份就必然有同步问题。</param>
/// <param name="PlainText">
/// 该便签的纯文本正文，来自 <c>SearchIndex.GetPlainText</c>。摘要从这里摘，
/// 不从 <see cref="Note.Content"/> 摘：后者带着 Markdown 标记，摘要里会冒出
/// <c>**</c> 和 <c>#</c>。
/// </param>
/// <param name="Query">
/// 当前的查询词。为空表示「没有在搜索」，副标题走「修改时间 · 字数」那一档。
/// </param>
public sealed record NoteListItem(Note Note, string PlainText, string? Query = null)
{
    /// <summary>便签 id。开窗、删除、选中都靠它。</summary>
    public Guid Id => Note.Id;

    /// <summary>标题，取自 <see cref="Note.Title"/>（自带缓存，见 §5.4）。</summary>
    public string Title => Note.Title;

    /// <summary>标签（§15.8 结果项里的「小徽章」）。</summary>
    /// <remarks>
    /// 直接交出 <see cref="Note.Tags"/> 这个列表本身，不复制、不排序。复制一份意味着
    /// 「便签加了标签但列表还显示旧的」这类不一致，而标签的数量与顺序都是用户自己定的
    /// （§5.8 按 Front Matter 里的原样保留），重排会让他认不出自己写的那一串。
    /// </remarks>
    public IReadOnlyList<string> Tags => Note.Tags;

    /// <summary>便签颜色（§15.8 结果项里的「颜色点」）。界面靠转换器把它换成笔刷。</summary>
    public NoteColor Color => Note.Color;

    /// <summary>
    /// 副标题要渲染的片段（§12.3、§15.8）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 有查询词且<strong>正文命中</strong>时是摘要：命中前后各 40 个字符、两端补省略号，
    /// 命中那一段 <see cref="SnippetSegment.IsMatch"/> 为真、由界面换成高亮色。
    /// </para>
    /// <para>
    /// 否则退回「修改时间 · 字数」。退回的条件比看起来宽：没有查询词、标题或标签命中、
    /// 以及查询词压根没命中正文，三种都算。后两种摘不出正文片段，而摘一段<em>不包含</em>
    /// 查询词的正文出来毫无意义——用户看不出这一段为什么在这儿。
    /// </para>
    /// <para>
    /// 退回的那一段也是普通的 <see cref="SnippetSegment"/>（<c>IsMatch</c> 为假），
    /// 于是界面只需要一个渲染循环，不必再判断「这一次画的是摘要还是日期」。
    /// </para>
    /// </remarks>
    public IReadOnlyList<SnippetSegment> SubtitleSegments
    {
        get
        {
            IReadOnlyList<SnippetSegment> snippet = SnippetBuilder.Build(PlainText, Query);

            return snippet.Count > 0 ? snippet : [new SnippetSegment(TimeAndLength, false)];
        }
    }

    /// <summary>副标题的纯文本形式（片段拼起来）。界面用的是 <see cref="SubtitleSegments"/>。</summary>
    public string Subtitle =>
        string.Concat(SubtitleSegments.Select(static segment => segment.Text));

    /// <summary>正文前两行，列表里作为预览。</summary>
    /// <remarks>
    /// 换行替成空格而不是保留：列表项高度固定在一行，
    /// 留着换行会让 WPF 按第一行算高度，后面的内容被裁掉而不显示省略号。
    /// </remarks>
    public string Preview
    {
        get
        {
            string flattened = Note.Content.ReplaceLineEndings(" ").Trim();

            return flattened.Length <= 120 ? flattened : flattened[..120] + "…";
        }
    }

    /// <summary>
    /// 「修改时间 · 字数」，形如 <c>9/18 11:19 · 50 字</c>（§15.8）。
    /// </summary>
    /// <remarks>
    /// 时间用 <see cref="DateTimeOffset.LocalDateTime"/> 而不是直接格式化
    /// <see cref="Note.UpdatedAt"/>：后者带的是 <c>+08:00</c> 这样的偏移，
    /// 默认格式会把它一并印出来，而用户只关心本地时间。
    /// </remarks>
    private string TimeAndLength =>
        $"{Note.UpdatedAt.LocalDateTime:M/d HH:mm} · {Note.Content.Length} 字";
}
