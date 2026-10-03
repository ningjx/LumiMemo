using LumiMemo.Core.Services;

namespace LumiMemo.Core.Models;

/// <summary>
/// 一条便签数据，对应磁盘上的一个 <c>.lumi</c> 文件（私有格式，2026-10 决策）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>便签是数据概念，不等于窗口</strong>——窗口由 UI 层的窗口类表示，
/// 一张便签最多有一个窗口（§0.2 术语表）。
/// </para>
/// <para>
/// <c>RichTextContent</c>（RTF）是<strong>权威内容</strong>——格式与图片都在里面；
/// <see cref="Content"/> 是纯文本投影，供标题派生、搜索与字数统计使用。
/// </para>
/// </remarks>
public sealed class Note
{
    /// <summary>稳定身份，来自文件的 <c>id</c> 字段——文件的唯一身份标识，不是文件名（§5.5）。</summary>
    public required Guid Id { get; init; }

    /// <summary><c>.lumi</c> 文件的完整路径。</summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// 纯文本投影：正文去掉格式之后的文字。标题派生、搜索、字数都用它。
    /// </summary>
    /// <remarks>
    /// setter 自己负责让 <see cref="Title"/> 的缓存失效，调用方<strong>不需要</strong>记得做任何事。
    /// 这一点是刻意的：失效逻辑若交给调用方，任何直接写 <c>note.Content = x</c> 的地方
    /// 漏掉一步就会静默读出旧标题——不报错、不抛异常，只是标题不对，属于最难查的一类缺陷。
    /// 把不变量收回类型内部，编译器才能替我们守住它。
    /// </remarks>
    public string Content
    {
        get => _content;
        set
        {
            // 值没变就不动缓存。编辑区每次按键都会走一遍 setter，包括「删掉又打回同一个字」的情况。
            if (string.Equals(_content, value, StringComparison.Ordinal))
            {
                return;
            }

            _content = value;
            _titleCache = null;
        }
    }

    private string _content = string.Empty;
    private string? _titleCache;

    /// <summary>
    /// 权威内容字节（不透明，VM 不解析）——格式与内嵌图片都在里面。
    /// 格式由编辑器内核决定：旧内核为 RTF 字节，新内核（LumiEditor）为 v2 JSON 字节。
    /// </summary>
    public byte[] RichTextContent { get; set; } = [];

    /// <summary>
    /// 派生属性：不在存储中，<see cref="Content"/> 一变就重算（§5.4）。
    /// </summary>
    /// <remarks>
    /// 标题完全派生、不存储，与「文件名即标题」的心智模型一致：
    /// 正文一改，标题立刻跟上，不需要同步额外字段。
    /// </remarks>
    public string Title => string.IsNullOrWhiteSpace(AutoTitle)
        ? _titleCache ??= TitleDeriver.Derive(_content)
        : AutoTitle;

    /// <summary>可选的 AI 标题；空值时继续从正文派生。</summary>
    public string? AutoTitle { get; set; }

    /// <summary>便签颜色。属于<strong>用户数据</strong>，跟着文件走。</summary>
    public NoteColor Color { get; set; } = NoteColor.Yellow;

    /// <summary>标签。属于<strong>用户数据</strong>。</summary>
    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
