using LumiMemo.Core.Services;

namespace LumiMemo.Core.Models;

/// <summary>
/// 一条便签数据，对应磁盘上的一个 <c>.md</c> 文件（§9.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>便签是数据概念，不等于窗口</strong>——窗口由 App 层的 <c>NoteWindow</c> 表示，
/// 一张便签最多有一个窗口（§0.2 术语表）。
/// </para>
/// <para>
/// <see cref="NoteStore"/> 中持有的是本类型的唯一实例，ViewModel 拿到的是同一个引用，
/// 因此不存在「两份数据」问题（§18.4）。
/// </para>
/// </remarks>
public sealed class Note
{
    /// <summary>稳定身份，来自 Front Matter 的 <c>id</c>。文件的唯一身份标识，不是文件名（§5.5）。</summary>
    public required Guid Id { get; init; }

    /// <summary>Markdown 文件的完整路径。移动便签时变化，<see cref="Id"/> 不变（§5.5）。</summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// 正文（不含 Front Matter）。
    /// 这是<strong>用户数据</strong>，是唯一权威内容来源。
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
            // 值没变就不动缓存。绑定侧 UpdateSourceTrigger=PropertyChanged 会在
            // 每次按键时走一遍 setter，包括「删掉又打回同一个字」的情况。
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
    /// 派生属性：不在存储中，<see cref="Content"/> 一变就重算（§5.4）。
    /// </summary>
    /// <remarks>
    /// 标题完全派生、不存储，与 Obsidian 的「文件名即标题」心智模型一致：
    /// 用户在外部编辑器里改的正文立刻反映到标题，不需要同步 Front Matter。
    /// </remarks>
    public string Title => _titleCache ??= TitleDeriver.Derive(_content);

    /// <summary>便签颜色。属于<strong>用户数据</strong>，跟着文件走（§5.3）。</summary>
    public NoteColor Color { get; set; } = NoteColor.Yellow;

    /// <summary>标签。属于<strong>用户数据</strong>，写入 Front Matter 的 <c>tags</c>（§5.8）。</summary>
    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>文件原有的行尾风格，写回时沿用（§5.9）。</summary>
    public LineEnding LineEnding { get; set; } = LineEnding.CrLf;

    /// <summary>解析时文件是否带 BOM。写回时原样加回去（§5.9）。</summary>
    public bool HadBom { get; set; }

    /// <summary>
    /// 结束分隔符之后、正文之前的结构形态。写回时据此逐字节还原（§5.2、§5.9）。
    /// </summary>
    /// <remarks>
    /// 语义与存在理由见 <see cref="FrontMatterTail"/>。新建便签保持默认值即可，
    /// 那正是 §5.2 定义的标准形态。
    /// </remarks>
    public FrontMatterTail FrontMatterTail { get; set; } = FrontMatterTail.LineBreakAndBlankLine;

    /// <summary>
    /// Front Matter 中本程序不认识的键，解析时保留、写回时原样输出（§5.3）。
    /// </summary>
    /// <remarks>
    /// 必须是保序容器——用 <c>Dictionary</c> 会导致写回顺序随机、git diff 抖动。
    /// 用户的 Obsidian vault 里往往有 <c>aliases</c>、<c>cssclasses</c> 或 Dataview 插件的字段，
    /// 静默丢弃它们等于破坏用户数据（§5.3）。
    /// </remarks>
    public List<KeyValuePair<string, object?>> UnknownFrontMatterKeys { get; set; } = [];

    /// <summary>解析过程中发现的异常（编码异常、YAML 损坏等），用于在 UI 上提示（§5.10）。</summary>
    public List<NoteParseIssue> ParseIssues { get; set; } = [];

    /// <summary>
    /// 把另一张便签的字段搬进<strong>本实例</strong>，<see cref="Id"/> 不动。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 外部修改与整目录重扫都要用磁盘版覆盖内存版（§10.2、§11.4），而<strong>不能换掉对象本身</strong>：
    /// 便签窗口绑的是 <see cref="Note"/> 这一个引用，换实例会让窗口握着一个不在
    /// <see cref="Stores.NoteStore"/> 里的孤儿——用户在里面敲的字既进不了 Store 也存不下去，
    /// 而 <c>SaveNoteAsync</c> 按 id 找到的是另一个对象。字段搬进来，引用不变。
    /// </para>
    /// <para>
    /// <see cref="Tags"/>（以及另外两个列表）<strong>原地清空再填</strong>，不换对象：
    /// 它们的引用已经散出去了（<c>NoteListItem</c> 直接交出那份列表本身），
    /// 换掉列表对象会让那些引用停在旧数据上。<c>ApplyTagsEdit</c> 守的是同一条约定。
    /// </para>
    /// <para>
    /// <strong><see cref="FilePath"/> 也在搬运之列</strong>：它的值来自磁盘，
    /// 而调用方通常是按路径找到本实例的，所以正常情况下它前后相同。
    /// 万一不同，磁盘上的那份才是事实。
    /// </para>
    /// </remarks>
    public void CopyFrom(Note other)
    {
        ArgumentNullException.ThrowIfNull(other);

        FilePath = other.FilePath;
        Content = other.Content;
        Color = other.Color;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        LineEnding = other.LineEnding;
        HadBom = other.HadBom;
        FrontMatterTail = other.FrontMatterTail;

        Replace(Tags, other.Tags);
        Replace(UnknownFrontMatterKeys, other.UnknownFrontMatterKeys);
        Replace(ParseIssues, other.ParseIssues);

        static void Replace<T>(List<T> target, List<T> source)
        {
            target.Clear();
            target.AddRange(source);
        }
    }

    /// <summary>
    /// 两张便签的<strong>用户数据</strong>是不是同一份（§0.2 划定的那一类字段）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比的是正文、颜色、标签三样——用户在界面上看得见、改得动的东西。刻意<strong>不比</strong>
    /// 时间戳、行尾、BOM、Front Matter 结构：那些只是文件的形态，同一份内容被另一个编辑器
    /// 存过一遍就会变，据此判「内容变了」会让整批文件白重载一次。
    /// </para>
    /// <para>
    /// 整目录重扫（<c>NoteService.LoadAllAsync</c>）用它回答「这个文件相对内存变了没有」——
    /// 重扫时仓储的「上次同步字节」已经被这次扫描自己刷新掉了，只能拿内容比。
    /// </para>
    /// </remarks>
    public bool HasSameUserDataAs(Note other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Color == other.Color
            && string.Equals(Content, other.Content, StringComparison.Ordinal)
            && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);
    }
}
