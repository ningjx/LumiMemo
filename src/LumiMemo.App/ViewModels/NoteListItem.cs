using LumiMemo.Core.Models;

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
public sealed record NoteListItem(Note Note)
{
    /// <summary>便签 id。开窗、删除、选中都靠它。</summary>
    public Guid Id => Note.Id;

    /// <summary>标题，取自 <see cref="Note.Title"/>（自带缓存，见 §5.4）。</summary>
    public string Title => Note.Title;

    /// <summary>
    /// 副标题：「修改时间 · 字数」，形如 <c>9/18 11:19 · 50 字</c>（§15.8）。
    /// </summary>
    /// <remarks>
    /// 时间用 <see cref="DateTimeOffset.LocalDateTime"/> 而不是直接格式化
    /// <see cref="Note.UpdatedAt"/>：后者带的是 <c>+08:00</c> 这样的偏移，
    /// 默认格式会把它一并印出来，而用户只关心本地时间。
    /// </remarks>
    public string Subtitle =>
        $"{Note.UpdatedAt.LocalDateTime:M/d HH:mm} · {Note.Content.Length} 字";

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
}
