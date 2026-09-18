namespace LumiMemo.Core.Models;

/// <summary>
/// 解析器（§5.2）的输出，是**尚未绑定 id 与路径**的中间形态（§14.2）。
/// </summary>
/// <remarks>
/// 把「解析内容」和「绑定身份」分成两步，是为了让解析器能纯函数化、脱离文件系统单测（§21.1）。
/// 绑定 <c>Id</c> 与 <see cref="Note.FilePath"/> 由仓储层在拿到真实路径后完成。
/// </remarks>
public sealed class NoteReadResult
{
    /// <summary>
    /// 正文（不含 Front Matter），原样保存不做规范化（§5.2）。
    /// </summary>
    /// <remarks>
    /// 文件末尾是否有换行、换行风格这些信息由本字符串自身携带——§5.9 要求
    /// 「原文件没有结尾换行就不写」，所以<strong>不要</strong>在解析时 Trim 这个值。
    /// </remarks>
    public required string Content { get; init; }

    /// <summary>文件原有的行尾风格，写回时沿用（§5.9）。</summary>
    public LineEnding LineEnding { get; init; } = LineEnding.CrLf;

    /// <summary>解析时文件是否带 BOM。写回时原样加回去（§5.9）。</summary>
    public bool HadBom { get; init; }

    /// <summary>
    /// 结束分隔符之后、正文之前的结构形态。写回时据此逐字节还原（§5.2、§5.9）。
    /// </summary>
    /// <remarks>
    /// 它是「四者原样保留」中「末尾换行」的那一角，理由见 <see cref="FrontMatterTail"/>。
    /// </remarks>
    public FrontMatterTail FrontMatterTail { get; init; } = FrontMatterTail.LineBreakAndBlankLine;

    /// <summary>
    /// Front Matter 中本程序不认识的键，解析时保留、写回时按原顺序输出（§5.3）。
    /// </summary>
    /// <remarks>
    /// 必须是保序容器。用 <c>Dictionary</c> 会导致写回顺序随机、git diff 抖动（§5.3）。
    /// </remarks>
    public List<KeyValuePair<string, object?>> UnknownFrontMatterKeys { get; init; } = [];

    /// <summary>解析过程中发现的异常，用于在 UI 上提示并写日志（§5.10）。</summary>
    public List<NoteParseIssue> ParseIssues { get; init; } = [];

    /// <summary>
    /// Front Matter 中的 <c>id</c>。<c>null</c> 表示缺失或不是合法 GUID，
    /// 由启动补给流程生成新值并回写（§5.5）。
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>Front Matter 中的 <c>color</c>。缺失或非法时用设置里的默认色（§5.10）。</summary>
    public NoteColor? Color { get; init; }

    /// <summary>Front Matter 中的 <c>tags</c>，已按 §5.8 规范化。空表示没有标签。</summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Front Matter 中的 <c>createdAt</c>。<c>null</c> 表示缺失或无法解析，
    /// 此时由仓储层退回文件系统的创建时间，再退回修改时间（§5.10）。
    /// </summary>
    /// <remarks>
    /// 这里用可空类型而不是就地填默认值，是因为解析器是纯函数、看不到文件系统时钟——
    /// 「用文件系统时间戳」这条降级只能由拥有文件系统访问权的仓储层完成。
    /// </remarks>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Front Matter 中的 <c>updatedAt</c>。语义同 <see cref="CreatedAt"/>。</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
