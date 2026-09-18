using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LumiMemo.Core.Services;

/// <summary>
/// 从正文派生标题（§5.4）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>标题完全派生、不存储</strong>。Front Matter 里没有 <c>title</c> 字段，
/// 标题永远是正文的投影。这样在 Obsidian 里改了正文第一行，标题立刻跟着变，
/// 不需要同步两处，也不存在「两处 title 不一致」的冲突（§5.4）。
/// </para>
/// <para>
/// 因此 UI 上<strong>不允许直接编辑标题</strong>——用户想改标题就去改正文第一行。
/// 这与 Obsidian、Bear、Notion 的行为一致。
/// </para>
/// <para>
/// 本类是纯函数，没有状态、不进 DI 容器（§14.2 的「没有接口」组）。
/// <see cref="Models.Note.Title"/> 直接静态调用它。开销由 <c>Note</c> 内部的缓存吸收，
/// 只在正文变更时重算。
/// </para>
/// <para>
/// 参数是<strong>正文</strong>，不含 Front Matter——Front Matter 由解析器剥离（§5.2）。
/// </para>
/// </remarks>
public static partial class TitleDeriver
{
    /// <summary>派生结果为空时使用的占位标题（§5.4、§5.6）。</summary>
    public const string EmptyTitle = "无标题";

    /// <summary>标题截断长度，按 Unicode 文本元素计数（§5.4）。</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// 从正文派生标题。
    /// </summary>
    /// <param name="content">正文，不含 Front Matter。可以是 <c>null</c> 或空串。</param>
    /// <returns>派生出的标题；结果为空时返回 <see cref="EmptyTitle"/>。永不为 <c>null</c>。</returns>
    public static string Derive(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return EmptyTitle;
        }

        var line = FindTitleLine(content);
        if (line is null)
        {
            return EmptyTitle;
        }

        var stripped = StripMarkdownMarkers(line);
        var normalized = CollapseWhitespace(stripped);

        return normalized.Length == 0 ? EmptyTitle : Truncate(normalized, MaxLength);
    }

    /// <summary>
    /// 取正文中第一行「有内容」的行（§5.4 第 1 步）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 跳过四类不构成标题的行：纯空行、HTML 注释、单独成行的图片、表格分隔行。
    /// 表格分隔行（<c>|---|</c>）也要跳过——否则一张以表格开头的便签，
    /// 标题会变成 <c>|---|</c> 这种毫无意义的字符串（§21.2）。
    /// </para>
    /// <para>
    /// 代码围栏要跳过<strong>整块</strong>而不只是围栏那一行（§21.2）：
    /// 以 <c>```</c> 开头的便签，标题应当来自围栏块之后的正文，
    /// 而不是把围栏里第一行代码当成标题。
    /// </para>
    /// </remarks>
    private static string? FindTitleLine(string content)
    {
        string? openFence = null;

        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();

            if (openFence is not null)
            {
                if (IsClosingFence(line, openFence))
                {
                    openFence = null;
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (TryGetFenceMarker(line, out var marker))
            {
                openFence = marker;
                continue;
            }

            if (IsHtmlComment(line) || IsImageOnlyLine(line) || IsTableSeparator(line))
            {
                continue;
            }

            return line.ToString();
        }

        return null;
    }

    /// <summary>整行是 HTML 注释，例如 <c>&lt;!-- 待办 --&gt;</c>。</summary>
    private static bool IsHtmlComment(ReadOnlySpan<char> line) =>
        line.StartsWith("<!--", StringComparison.Ordinal) && line.EndsWith("-->", StringComparison.Ordinal);

    /// <summary>
    /// 单独成行的图片，例如 <c>![截图](attachments/xxx.png)</c>。
    /// 图片不是一个有意义的标题来源（§5.4）。
    /// </summary>
    private static bool IsImageOnlyLine(ReadOnlySpan<char> line) =>
        line.StartsWith("![", StringComparison.Ordinal) && line.EndsWith(")", StringComparison.Ordinal);

    /// <summary>
    /// 判断一行是不是代码围栏（<c>```</c> 或 <c>~~~</c>，至少三个），并交出围栏标记。
    /// </summary>
    /// <param name="line">已 trim 的一行。</param>
    /// <param name="marker">围栏标记本身，例如 <c>```</c>。用于与收尾围栏配对。</param>
    /// <remarks>
    /// 允许围栏后跟语言标记（<c>```csharp</c>），因此只取前导的连续标记字符。
    /// </remarks>
    private static bool TryGetFenceMarker(ReadOnlySpan<char> line, out string marker)
    {
        marker = string.Empty;

        if (line.Length < 3)
        {
            return false;
        }

        var fenceChar = line[0];
        if (fenceChar != '`' && fenceChar != '~')
        {
            return false;
        }

        var count = 0;
        while (count < line.Length && line[count] == fenceChar)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        marker = new string(fenceChar, count);
        return true;
    }

    /// <summary>
    /// 判断一行是不是给定围栏的收尾行。
    /// </summary>
    /// <remarks>
    /// 要求同种字符、长度不短于起始围栏、且整行只有围栏本身——
    /// 否则 <c>```csharp</c> 会被误判成收尾，围栏块从第二行就"结束"了。
    /// </remarks>
    private static bool IsClosingFence(ReadOnlySpan<char> line, string openFence) =>
        TryGetFenceMarker(line, out var marker)
        && marker[0] == openFence[0]
        && marker.Length >= openFence.Length
        && line.Length == marker.Length;

    /// <summary>
    /// 表格分隔行，例如 <c>|---|:--:|---|</c> 或 <c>---</c>。
    /// 判定方式：整行只由 <c>|</c>、<c>-</c>、<c>:</c> 与空白组成，且至少含一个 <c>-</c>。
    /// </summary>
    private static bool IsTableSeparator(ReadOnlySpan<char> line)
    {
        var hasDash = false;

        foreach (var ch in line)
        {
            switch (ch)
            {
                case '-':
                    hasDash = true;
                    break;
                case '|':
                case ':':
                case ' ':
                case '\t':
                    break;
                default:
                    return false;
            }
        }

        return hasDash;
    }

    /// <summary>剥离行内 Markdown 标记（§5.4 第 2 步）。</summary>
    private static string StripMarkdownMarkers(string line)
    {
        var text = line;

        // 行首标记可能叠加，例如 "> - [ ] 待办" 或 "1. **重点**"。
        // 每轮剥掉一层，直到稳定为止。
        for (var pass = 0; pass < 4; pass++)
        {
            var before = text;

            text = BlockquoteMarker().Replace(text, string.Empty);
            text = ListMarker().Replace(text, string.Empty);
            text = HeadingMarker().Replace(text, string.Empty);
            text = TaskCheckbox().Replace(text, string.Empty);
            text = text.TrimStart();

            if (string.Equals(before, text, StringComparison.Ordinal))
            {
                break;
            }
        }

        // 行内格式：链接保留文字、强调符号与行内代码的反引号去掉、HTML 标签去掉只留文本。
        //
        // 注意 $1 / $2 的区别：粗体与斜体的正则里第 1 组是**标记本身**（用于反向引用配对），
        // 第 2 组才是内容，所以取 $2。链接与行内代码没有标记分组，内容就是 $1。
        text = InlineLink().Replace(text, "$1");
        text = InlineHtmlTag().Replace(text, string.Empty);
        text = InlineCode().Replace(text, "$1");
        text = StrongEmphasis().Replace(text, "$2");
        text = Emphasis().Replace(text, "$2");

        return text;
    }

    /// <summary>合并连续空白为单个空格并 trim（§5.4 第 3 步）。</summary>
    private static string CollapseWhitespace(string text) =>
        WhitespaceRun().Replace(text, " ").Trim();

    /// <summary>
    /// 按 Unicode 文本元素截断（§5.4 第 4 步）。
    /// </summary>
    /// <remarks>
    /// <strong>不能按 <c>char</c> 切</strong>：emoji 与中文组合字符由多个 <c>char</c> 组成，
    /// 按 char 截断会切出半个代理对，产生乱码。
    /// </remarks>
    private static string Truncate(string text, int maxTextElements)
    {
        var indexes = StringInfo.ParseCombiningCharacters(text);
        if (indexes.Length <= maxTextElements)
        {
            return text;
        }

        return text[..indexes[maxTextElements]];
    }

    // ---- 正则。全部用 GeneratedRegex 在编译期生成，避免运行时光是解析正则的开销（§2.2 的同类理由）。----

    /// <summary>行首的引用标记，可重复，例如 <c>&gt; &gt; </c>。</summary>
    [GeneratedRegex(@"^(?:>\s*)+", RegexOptions.None)]
    private static partial Regex BlockquoteMarker();

    /// <summary>行首的列表标记：<c>- </c>、<c>* </c>、<c>+ </c>、<c>1. </c>、<c>1) </c>。</summary>
    [GeneratedRegex(@"^(?:[-*+]\s+|\d+[.)]\s+)", RegexOptions.None)]
    private static partial Regex ListMarker();

    /// <summary>行首的标题标记。<c>\s*</c> 而非 <c>\s+</c>，这样单独一行的 <c>#</c> 也能被剥掉（§21.2）。</summary>
    [GeneratedRegex(@"^#{1,6}\s*", RegexOptions.None)]
    private static partial Regex HeadingMarker();

    /// <summary>任务复选框：<c>[ ] </c>、<c>[x] </c>、<c>[X] </c>。</summary>
    [GeneratedRegex(@"^\[[ xX]\]\s*", RegexOptions.None)]
    private static partial Regex TaskCheckbox();

    /// <summary>行内链接，保留显示文字，丢掉目标。</summary>
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.None)]
    private static partial Regex InlineLink();

    /// <summary>行内 HTML 标签，去掉标签只留文本。</summary>
    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.None)]
    private static partial Regex InlineHtmlTag();

    /// <summary>行内代码，保留反引号里的内容。</summary>
    [GeneratedRegex(@"`([^`]*)`", RegexOptions.None)]
    private static partial Regex InlineCode();

    /// <summary>成对的粗体与删除线标记。</summary>
    [GeneratedRegex(@"(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1", RegexOptions.None)]
    private static partial Regex StrongEmphasis();

    /// <summary>成对的斜体标记。</summary>
    [GeneratedRegex(@"(\*|_)(?=\S)(.+?)(?<=\S)\1", RegexOptions.None)]
    private static partial Regex Emphasis();

    /// <summary>连续空白。</summary>
    [GeneratedRegex(@"\s+", RegexOptions.None)]
    private static partial Regex WhitespaceRun();
}
