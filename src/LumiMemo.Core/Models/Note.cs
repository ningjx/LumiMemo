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
}
