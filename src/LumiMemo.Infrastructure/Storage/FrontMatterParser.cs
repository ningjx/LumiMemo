using System.Globalization;
using System.Text;
using LumiMemo.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 把一个便签文件的<strong>原始字节</strong>解析成 <see cref="NoteReadResult"/>（§5.2、§5.10）。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数，不碰文件系统，因此可以在 Core.Tests 里脱离磁盘单测（§21.1）。
/// 唯一的例外是「用文件创建时间兜底」——那件事需要文件系统，属于仓储层的职责，
/// 所以本解析器把缺失的时间留成 <see langword="null"/>（见 <see cref="NoteReadResult.CreatedAt"/>）。
/// </para>
/// <para>
/// <strong>本方法对任何字节序列都不抛异常。</strong>便签文件处在系统边界上：
/// 用户会用别的编辑器改它、网盘会把它同步成半截、别的插件会往里写乱七八糟的东西。
/// 一个坏文件绝不能让整次扫描失败（§5.10），所以所有异常出口都在这里被接住，
/// 转化为 <see cref="NoteParseIssue"/> 挂在结果上。
/// </para>
/// </remarks>
public static class FrontMatterParser
{
    /// <summary>独占一行的 Front Matter 分隔符（§5.2）。</summary>
    public const string Delimiter = "---";

    /// <summary>超过这个大小仍能加载，但不再参与全文搜索（§5.10）。</summary>
    public const int FullTextSearchSizeLimit = 1024 * 1024;

    private static readonly NoteParseIssueKind InvalidYaml = NoteParseIssueKind.InvalidYaml;

    // 严格模式的 UTF-8：遇到非法字节抛 DecoderFallbackException，这正是探测
    // 「这文件根本不是 UTF-8」所需要的行为。默认的 UTF8Encoding 会把非法字节
    // 换成 U+FFFD 静默读下去，之后就再也分不清是自己读错了还是文件本来就长这样。
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    /// <summary>解析一个便签文件的原始字节。</summary>
    /// <param name="raw">文件全部字节。</param>
    /// <param name="ansiFallback">
    /// 非法 UTF-8 时使用的回退编码。传 <see langword="null"/> 用系统 ANSI 代码页（§5.9）。
    /// 开放这个参数是为了让测试能钉住某个具体代码页，不依赖跑测试那台机器的区域设置。
    /// </param>
    public static ParsedNoteFile Parse(byte[] raw, NoteEncodingProfile? ansiFallback = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        List<NoteParseIssue> issues = [];
        (string text, NoteEncodingProfile profile) = Decode(raw, ansiFallback, issues);

        if (raw.Length > FullTextSearchSizeLimit)
        {
            issues.Add(new NoteParseIssue(
                NoteParseIssueKind.FileTooLarge,
                $"文件 {raw.Length.ToString(CultureInfo.InvariantCulture)} 字节，超过 "
                + $"{FullTextSearchSizeLimit.ToString(CultureInfo.InvariantCulture)} 字节，将不参与全文搜索。"));
        }

        LineEnding lineEnding = DetectLineEnding(text);

        // 0 字节文件当作一张空便签加载（§5.10）。它没有 Front Matter，
        // 也没有正文——后续由仓储层补给 id 并写入 Front Matter。
        if (text.Length == 0)
        {
            return new ParsedNoteFile(
                new NoteReadResult
                {
                    Content = string.Empty,
                    LineEnding = lineEnding,
                    HadBom = profile.HasBom,
                    ParseIssues = issues,
                },
                profile,
                FrontMatterUnparsable: false);
        }

        FrontMatterSplit split = SplitFrontMatter(text);

        NoteFields fields = split.Found
            ? ParseFields(split.FrontMatter, issues)
            : new NoteFields();

        var result = new NoteReadResult
        {
            Content = split.Body,
            LineEnding = lineEnding,
            HadBom = profile.HasBom,
            FrontMatterTail = split.Tail,

            // 未知键存的是 YamlNode 本身（boxed 成 object）。刻意不做
            // 「反序列化成 Dictionary/List」的转换：那样嵌套映射会退化成无序的
            // Dictionary，写回时顺序随机变化，在 git 里表现为无意义的 diff 抖动（§5.3）。
            // YamlNode 保留了顺序、结构与标量样式，是保真的最小代价方案。
            UnknownFrontMatterKeys = fields.UnknownKeys,
            ParseIssues = issues,
            Id = fields.Id,
            Color = fields.Color,
            Tags = fields.Tags,
            CreatedAt = fields.CreatedAt,
            UpdatedAt = fields.UpdatedAt,
        };

        return new ParsedNoteFile(result, profile, split.Found && fields.Unparsable);
    }

    // ---- 编码探测（§5.9） ----

    private static (string Text, NoteEncodingProfile Profile) Decode(
        byte[] raw,
        NoteEncodingProfile? ansiFallback,
        List<NoteParseIssue> issues)
    {
        if (StartsWith(raw, Utf8Bom))
        {
            return (NoteEncodingProfile.Utf8WithBom.Encoding.GetString(raw, Utf8Bom.Length, raw.Length - Utf8Bom.Length), NoteEncodingProfile.Utf8WithBom);
        }

        // UTF-16 的 BOM 必须单独识别，不能让它掉进下面的 ANSI 回退：
        // 那样整个文件会被读成一片乱码，用户一编辑就把乱码写回磁盘。
        // 它同样属于 §5.9 的「不是合法 UTF-8」，因此照样标记编码异常。
        if (StartsWith(raw, Utf16LeBom))
        {
            issues.Add(new NoteParseIssue(NoteParseIssueKind.InvalidEncoding, "文件是 UTF-16（小端）编码，不是 UTF-8。"));
            return (NoteEncodingProfile.Utf16Le.Encoding.GetString(raw, Utf16LeBom.Length, raw.Length - Utf16LeBom.Length), NoteEncodingProfile.Utf16Le);
        }

        if (StartsWith(raw, Utf16BeBom))
        {
            issues.Add(new NoteParseIssue(NoteParseIssueKind.InvalidEncoding, "文件是 UTF-16（大端）编码，不是 UTF-8。"));
            return (NoteEncodingProfile.Utf16Be.Encoding.GetString(raw, Utf16BeBom.Length, raw.Length - Utf16BeBom.Length), NoteEncodingProfile.Utf16Be);
        }

        try
        {
            return (StrictUtf8.GetString(raw), NoteEncodingProfile.Utf8);
        }
        catch (DecoderFallbackException)
        {
            NoteEncodingProfile ansi = ansiFallback ?? NoteEncodingProfile.SystemAnsi;
            issues.Add(new NoteParseIssue(
                NoteParseIssueKind.InvalidEncoding,
                $"文件不是合法 UTF-8，已按 {ansi.Encoding.WebName} 回退读取（§5.9）。建议用 UTF-8 重新保存。"));
            return (ansi.Encoding.GetString(raw), ansi);
        }
    }

    private static bool StartsWith(byte[] raw, byte[] prefix)
    {
        if (raw.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (raw[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 探测行尾风格（§5.9）：含 <c>\r\n</c> 视为 CRLF，否则 LF。
    /// </summary>
    /// <remarks>
    /// 一个换行都没有的文件视为 CRLF：此时没有「原有风格」可保留，
    /// 于是采用 §5.9 给新建便签定的默认组合（UTF-8 无 BOM + CRLF）。
    /// </remarks>
    private static LineEnding DetectLineEnding(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? LineEnding.CrLf
        : text.Contains('\n', StringComparison.Ordinal) ? LineEnding.Lf
        : text.Contains('\r', StringComparison.Ordinal) ? LineEnding.Lf
        : LineEnding.CrLf;

    // ---- Front Matter 切分（§5.2） ----

    private readonly record struct FrontMatterSplit(
        bool Found,
        string FrontMatter,
        string Body,
        FrontMatterTail Tail);

    /// <summary>
    /// 把文件切成 Front Matter 与正文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正文的起点是「结束分隔符那一行的换行符<strong>之后</strong>」，并且再吃掉紧随其后的
    /// <strong>一个</strong>空行——那个空行是 §5.2 规定的格式，不是用户内容。
    /// 它的有无由 <see cref="FrontMatterTail"/> 记着，写回时照原样补出来；
    /// 多出来的空行（用户自己敲的）则留在正文里，一个字都不动。
    /// </para>
    /// <para>
    /// 开分隔符存在但找不到结束分隔符时，按 §5.10 的「Front Matter 未闭合」处理：
    /// 整份文件当正文，且 <see cref="FrontMatterSplit.Found"/> 为 <see langword="false"/>，
    /// 告诉上层补给 id 时要用「追加」而不是「替换」策略。
    /// </para>
    /// </remarks>
    private static FrontMatterSplit SplitFrontMatter(string text)
    {
        int openEnd = ReadLine(text, 0, out int openBreakLength);

        // 开分隔符不存在，或文件里只有它一行（后面再没有内容可以承载结束分隔符）。
        if (!IsDelimiterLine(text, 0, openEnd) || openEnd + openBreakLength >= text.Length)
        {
            return new FrontMatterSplit(false, string.Empty, text, FrontMatterTail.LineBreakOnly);
        }

        int frontMatterStart = openEnd + openBreakLength;
        int position = frontMatterStart;

        while (position < text.Length)
        {
            int lineEnd = ReadLine(text, position, out int breakLength);

            if (IsDelimiterLine(text, position, lineEnd))
            {
                string frontMatter = text[frontMatterStart..position];

                // 结束分隔符正好是文件末尾，后面什么都没有。
                // 这一形态必须与「分隔符后面有一个换行」区分开，否则写回时会
                // 凭空多补一个换行（见 FrontMatterTail）。
                if (breakLength == 0)
                {
                    return new FrontMatterSplit(true, frontMatter, string.Empty, FrontMatterTail.None);
                }

                int afterDelimiter = lineEnd + breakLength;

                if (TryConsumeLineBreak(text, afterDelimiter, out int blankBreakLength))
                {
                    return new FrontMatterSplit(
                        true,
                        frontMatter,
                        text[(afterDelimiter + blankBreakLength)..],
                        FrontMatterTail.LineBreakAndBlankLine);
                }

                return new FrontMatterSplit(true, frontMatter, text[afterDelimiter..], FrontMatterTail.LineBreakOnly);
            }

            if (breakLength == 0)
            {
                break;
            }

            position = lineEnd + breakLength;
        }

        // 开分隔符有、结束分隔符没有：整份文件当正文（§5.10）。
        return new FrontMatterSplit(false, string.Empty, text, FrontMatterTail.LineBreakOnly);
    }

    /// <summary>判断 <paramref name="start"/> 处是否就是一个行尾换行，是则给出它的长度。</summary>
    /// <remarks>
    /// 与 <see cref="ReadLine"/> 的分工：<c>ReadLine</c> 问「这一行到哪结束」，
    /// 本方法问「这里本身是不是一个换行」——判断「分隔符后面那一行是不是空行」时用的是后者。
    /// </remarks>
    private static bool TryConsumeLineBreak(string text, int start, out int length)
    {
        length = 0;

        if (start >= text.Length)
        {
            return false;
        }

        if (text[start] == '\r')
        {
            length = start + 1 < text.Length && text[start + 1] == '\n' ? 2 : 1;
            return true;
        }

        if (text[start] == '\n')
        {
            length = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读出 <paramref name="start"/> 处这一行的内容范围。
    /// </summary>
    /// <param name="lineBreakLength">
    /// 行尾换行符的长度：<c>2</c>（<c>\r\n</c>）、<c>1</c>（单独的 <c>\n</c> 或 <c>\r</c>）、
    /// <c>0</c>（这一行就是文件末尾，后面没有换行）。
    /// </param>
    /// <returns>行内容结束下标（不含换行符）。</returns>
    private static int ReadLine(string text, int start, out int lineBreakLength)
    {
        int end = start;
        while (end < text.Length && text[end] is not ('\r' or '\n'))
        {
            end++;
        }

        lineBreakLength = 0;
        if (end < text.Length)
        {
            lineBreakLength = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
        }

        return end;
    }

    private static bool IsDelimiterLine(string text, int start, int end) =>
        end - start == Delimiter.Length
        && string.CompareOrdinal(text, start, Delimiter, 0, Delimiter.Length) == 0;

    // ---- Front Matter 字段（§5.3） ----

    private sealed class NoteFields
    {
        public Guid? Id { get; set; }

        public NoteColor? Color { get; set; }

        public List<string> Tags { get; } = [];

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }

        public List<KeyValuePair<string, object?>> UnknownKeys { get; } = [];

        /// <summary>这段 Front Matter 没能解析成键值映射（语法错误，或根节点不是映射）。</summary>
        public bool Unparsable { get; set; }
    }

    private static NoteFields ParseFields(string frontMatter, List<NoteParseIssue> issues)
    {
        var fields = new NoteFields();

        YamlMappingNode? map = null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(frontMatter));

            if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode mapping)
            {
                map = mapping;
            }
            else
            {
                // 不是映射也按「读不懂」处理。此时若照常走替换流程，用户写的那几行
                // 会被当成「未知字段为零个」而在写回时静默消失；标记成读不懂，
                // 仓储层就会先备份再修复，那几行至少还在 .bak 里（§5.10）。
                fields.Unparsable = true;
                issues.Add(new NoteParseIssue(InvalidYaml, "Front Matter 不是一个键值映射。"));
            }
        }
        catch (YamlException ex)
        {
            fields.Unparsable = true;

            // 只记异常类型与位置，**绝不**把 ex.Message 写进日志：YamlDotNet 会把出错的
            // 那几行原文拼进消息里，用户的笔记内容会因此泄漏到日志文件中。
            // §5.10 要求此时「能解析出的字段照用」——YamlDotNet 在语法错误时
            // 交不出半份文档，于是全部字段走默认值，由上层决定是否备份并修复。
            issues.Add(new NoteParseIssue(
                InvalidYaml,
                $"YAML 语法错误：{ex.GetType().Name}，位置第 "
                + $"{ex.Start.Line.ToString(CultureInfo.InvariantCulture)} 行。"));
        }

        if (map is null)
        {
            return fields;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } key })
            {
                issues.Add(new NoteParseIssue(InvalidYaml, "Front Matter 中存在非字符串的键，已忽略。"));
                continue;
            }

            switch (key)
            {
                case "id":
                    fields.Id = ReadGuid(pair.Value);
                    break;
                case "color":
                    fields.Color = ReadColor(pair.Value);
                    break;
                case "createdAt":
                    fields.CreatedAt = ReadTimestamp(pair.Value);
                    break;
                case "updatedAt":
                    fields.UpdatedAt = ReadTimestamp(pair.Value);
                    break;
                case "tags":
                    ReadTags(pair.Value, fields.Tags, issues);
                    break;
                default:
                    fields.UnknownKeys.Add(new KeyValuePair<string, object?>(key, pair.Value));
                    break;
            }
        }

        return fields;
    }

    private static Guid? ReadGuid(YamlNode node) =>
        node is YamlScalarNode { Value: { } value } && Guid.TryParse(value, out Guid id) ? id : null;

    private static NoteColor? ReadColor(YamlNode node)
    {
        if (node is not YamlScalarNode { Value: { } value })
        {
            return null;
        }

        // 要求写出来的就是枚举名。单用 Enum.TryParse 会把 "3" 也认成 Gray——
        // 那是把用户写错的数字悄悄当成了合法颜色，不如退回默认色（§5.10）。
        return Enum.TryParse(value, ignoreCase: true, out NoteColor color)
            && string.Equals(color.ToString(), value, StringComparison.OrdinalIgnoreCase)
            ? color
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(YamlNode node) =>
        node is YamlScalarNode { Value: { } value }
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : null;

    private static void ReadTags(YamlNode node, List<string> tags, List<NoteParseIssue> issues)
    {
        switch (node)
        {
            case YamlSequenceNode sequence:
                foreach (YamlNode item in sequence.Children)
                {
                    if (item is YamlScalarNode { Value: { } value })
                    {
                        AddTag(tags, value);
                    }
                }

                break;

            case YamlScalarNode { Value: { } single }:
                // §5.10：tags 写成字符串时按单个标签处理，并记一条 Warning。
                // 这里借用 InvalidYaml 这个种类——它就是「语义不符合规范」的通用出口。
                AddTag(tags, single);
                issues.Add(new NoteParseIssue(InvalidYaml, "tags 不是数组，已按单个标签处理。"));
                break;

            case YamlMappingNode:
                issues.Add(new NoteParseIssue(InvalidYaml, "tags 是映射，已忽略。"));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 按 §5.8 规范化一个标签。
    /// </summary>
    /// <remarks>
    /// §5.8 说规范化「在写入时执行一次，之后按规范形式存储与比较」。这里在<strong>读</strong>的
    /// 时候就做，是为了让 <c>Note.Tags</c> 里永远是规范形式——否则「展示」「比较」「写回」
    /// 三处都得各自记着再规范化一遍，漏掉一处就是同一标签被当成两个。
    /// 长度上限（64 字符）不在这里执行：那是<strong>输入校验</strong>（拒绝并提示用户），
    /// 读文件时执行等于把用户已有的长标签直接丢掉，属于破坏数据。
    /// </remarks>
    private static void AddTag(List<string> tags, string raw)
    {
        string tag = NormalizeTag(raw);

        if (tag.Length == 0)
        {
            return;
        }

        foreach (string existing in tags)
        {
            if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        tags.Add(tag);
    }

    private static string NormalizeTag(string raw)
    {
        string value = raw.Trim();

        // 去掉 # 前缀，与 Obsidian 一致（§5.8）。
        if (value.StartsWith('#'))
        {
            value = value[1..].Trim();
        }

        // 内部空白替换为 -，"to read" → "to-read"（§5.8）。
        if (value.Contains(' ', StringComparison.Ordinal))
        {
            var builder = new StringBuilder(value.Length);
            bool lastWasDash = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasDash)
                    {
                        builder.Append('-');
                        lastWasDash = true;
                    }
                }
                else
                {
                    builder.Append(c);
                    lastWasDash = c == '-';
                }
            }

            value = builder.ToString();
        }

        return value;
    }
}
