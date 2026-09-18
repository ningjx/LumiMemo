using LumiMemo.Core.Models;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 一次文件解析的全部产出：<see cref="NoteReadResult"/> 加上<strong>只有存储层才关心的</strong>两项。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NoteReadResult"/> 是 Core 层的契约，只承载「便签内容」。
/// 下面两项都是文件形态而非便签内容，因此留在 Infrastructure 层。
/// </para>
/// <para>
/// <see cref="FrontMatterUnparsable"/> 是 §5.10「YAML 语法错误」那一行的触发条件：
/// 仓储层据此决定「先备份成 <c>.bak</c> 再写回修复版」，否则就会在不通知用户的情况下
/// 把一段读不懂的 Front Matter 直接抹掉。
/// </para>
/// <para>
/// 不能用「有没有 <see cref="NoteParseIssueKind.InvalidYaml"/>」代替这个判断：
/// <c>tags</c> 写成了字符串这种<strong>完全无害</strong>的情况也会记同一个种类，
/// 而那种文件既不该被备份、更不该被改写。
/// </para>
/// <para>
/// 另外注意它<strong>不是</strong>「文件是否有 Front Matter」：没有 Front Matter 的文件
/// （含未闭合的情形）这里同样是 <see langword="false"/>。补写 id 时要不要用「追加」策略
/// 并不需要这个标志——那类文件的正文本身就是整份文件，序列化器在前面拼上 Front Matter
/// 就自然成了追加（§5.10）。
/// </para>
/// </remarks>
/// <param name="Result">解析出的便签内容与字段。</param>
/// <param name="Encoding">文件在磁盘上的编码形态，写回时沿用（§5.9）。</param>
/// <param name="FrontMatterUnparsable">
/// 文件开头有闭合的 Front Matter，但它<strong>没能解析成一个键值映射</strong>。
/// </param>
public sealed record ParsedNoteFile(
    NoteReadResult Result,
    NoteEncodingProfile Encoding,
    bool FrontMatterUnparsable);
