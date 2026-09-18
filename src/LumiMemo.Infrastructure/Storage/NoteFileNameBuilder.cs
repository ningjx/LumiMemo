using System.Buffers;
using System.Globalization;
using System.Text;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 新建便签时的文件名派生（§5.6）。
/// </summary>
/// <remarks>
/// <para>
/// 格式为 <c>{标题摘要}-{创建日期}-{短ID}.md</c>，<strong>创建时确定，之后不自动重命名</strong>。
/// 文件名是「创建那一刻的快照」，便签真正的身份是 Front Matter 里的 <c>id</c>，
/// 真正的标题是正文第一行。三者不一致是<strong>设计如此</strong>，不是缺陷（§5.6）。
/// </para>
/// <para>
/// 之所以不跟随标题自动改名：每次标题变化都改名会产生大量重命名事件，与文件监听、
/// 去抖、自写抑制三套逻辑纠缠；同步工具对「重命名」的处理也会产生额外流量甚至冲突副本；
/// 用户在 Obsidian 里引用过该文件时还会打断链接（§5.6）。
/// </para>
/// <para>
/// 本类不碰文件系统——「文件是否已存在」由调用方以谓词形式传入，因此可以纯单测（§21.1）。
/// </para>
/// </remarks>
public static class NoteFileNameBuilder
{
    /// <summary>扩展名。小写，便于在任何平台上一致比较（§5.6）。</summary>
    public const string Extension = ".md";

    /// <summary>标题摘要的最大长度，按字符（Unicode 标量）计（§5.6）。</summary>
    public const int MaxSlugLength = 40;

    /// <summary>标题摘要为空时使用的中文兜底名（§5.6）。</summary>
    public const string UntitledSlug = "无标题";

    /// <summary>路径校验不通过时的兜底文件名（§19.4）。</summary>
    public const string FallbackStem = "untitled";

    /// <summary>Windows 保留设备名。不区分大小写，且带扩展名也算（§5.6）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>文件名里不允许出现的字符（§5.6、§19.4）。</summary>
    private static readonly SearchValues<char> IllegalCharacters = SearchValues.Create("<>:\"/\\|?*");

    /// <summary>
    /// 派生一个可用的完整文件路径，必要时自动避让重名。
    /// </summary>
    /// <param name="title">派生标题（§5.4）。空或空白时得到 <c>无标题</c>。</param>
    /// <param name="createdAt">创建时间，取本地日期的 <c>yyyyMMdd</c>。</param>
    /// <param name="id">便签的 <c>id</c>，取其前 8 位十六进制作短 ID。</param>
    /// <param name="directory">目标目录（笔记目录本身，或其中一个子文件夹）。</param>
    /// <param name="notesRoot">笔记目录根，用于 §19.4 的「不许逃出笔记目录」校验。</param>
    /// <param name="isTaken">判断某个完整路径是否已被占用；传 <see langword="null"/> 表示不做重名避让。</param>
    public static string Build(
        string title,
        DateTimeOffset createdAt,
        Guid id,
        string directory,
        string notesRoot,
        Func<string, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(notesRoot);

        string stem = string.Create(
            CultureInfo.InvariantCulture,
            $"{SanitizeSlug(title)}-{createdAt:yyyyMMdd}-{ShortId(id)}");

        string path = Path.Combine(directory, stem + Extension);

        // §19.4 的第二道校验。第一道（洗干净字符）挡不住 ".." 这类输入：
        // "../../../autoexec.md" 不含任何非法字符，但会把文件写到笔记目录之外。
        // 这里的兜底名不带日期和短 ID 之外的任何用户输入，因此不可能再逃逸。
        if (!IsInsideNotesRoot(path, notesRoot))
        {
            path = Path.Combine(directory, string.Create(
                CultureInfo.InvariantCulture,
                $"{FallbackStem}-{ShortId(id)}{Extension}"));
        }

        if (isTaken is null)
        {
            return path;
        }

        // 理论上短 ID 已经保证了唯一性，这里的避让是极小概率事件的保险（§5.6）。
        if (!isTaken(path))
        {
            return path;
        }

        string fullStem = Path.Combine(directory, stem);
        for (int suffix = 2; ; suffix++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{fullStem}-{suffix}{Extension}");

            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把标题洗成可以放进文件名的形式（§5.6）。
    /// </summary>
    /// <remarks>
    /// 顺序：删非法字符与控制字符 → 折叠连续空白和 <c>-</c> → 去首尾的 <c>.</c>/空格/<c>-</c>
    /// → 截断到 40 个字符 → 空则用 <c>无标题</c>。
    /// <para>
    /// 与文档表格的两处出入，都是为了得到一个用户真正能看的文件名：
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     制表符按<strong>空白</strong>处理而不是当控制字符删掉。制表符在标题里起着分隔词的作用，
    ///     删掉会把 <c>a&lt;Tab&gt;b</c> 粘成 <c>ab</c>；当成空白则得到 <c>a-b</c>，是用户预期的那个。
    ///   </item>
    ///   <item>
    ///     截断<strong>之后</strong>再去一次尾部的分隔符。文档的顺序（先去尾再截断）会留下
    ///     <c>很长的标题……-20260919-abc.md</c> 这种双划线段，纯属噪声。
    ///   </item>
    /// </list>
    /// </remarks>
    public static string SanitizeSlug(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var builder = new StringBuilder(title.Length);
        bool lastWasSeparator = false;

        foreach (char c in title)
        {
            if (IllegalCharacters.Contains(c) || (char.IsControl(c) && !char.IsWhiteSpace(c)))
            {
                continue;
            }

            if (char.IsWhiteSpace(c) || c == '-')
            {
                if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }

                continue;
            }

            builder.Append(c);
            lastWasSeparator = false;
        }

        string slug = TrimSeparators(builder.ToString());
        slug = TruncateByRunes(slug, MaxSlugLength);
        slug = TrimSeparators(slug);

        if (slug.Length == 0)
        {
            return UntitledSlug;
        }

        // §5.6：标题摘要命中保留字时追加 "_"。带扩展名也算，所以只看第一个 "." 之前的部分。
        return IsReservedName(slug) ? slug + "_" : slug;
    }

    /// <summary>
    /// 判断一个名字是否是 Windows 保留设备名（§5.6）。
    /// </summary>
    /// <param name="name">纯文件名，可以带扩展名（<c>CON.md</c> 同样算命中）。</param>
    public static bool IsReservedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? name : name[..dot];
        return ReservedNames.Contains(stem);
    }

    /// <summary>便签 <c>id</c> 的前 8 位小写十六进制（§5.6 的「短 ID」）。</summary>
    /// <remarks>
    /// 文档说短 ID 用「新 GUID」，没说是不是便签自己的 <c>id</c>。这里就取 <c>id</c> 本身——
    /// 少生成一个 GUID，而且文件名与 Front Matter 里的 <c>id</c> 能对上，
    /// 用户哪天要在文件堆里找某张便签时，这是唯一能用的线索。
    /// </remarks>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>校验 <paramref name="path"/> 确实落在 <paramref name="notesRoot"/> 里面（§19.4）。</summary>
    /// <remarks>
    /// 必须在<strong>规范化之后</strong>比较：规范化会把 <c>..</c> 消解掉，
    /// 拿未规范化的字符串作前缀比较是没用的。也正因如此，
    /// <c>\\?\</c> 长路径前缀必须等这一步过了再加（§19.4、§5.10 都强调了这点）。
    /// 两个路径都用 <see cref="Path.GetFullPath(string)"/> 拉平，
    /// 否则「笔记目录是相对路径」时前缀永远对不上。
    /// </remarks>
    public static bool IsInsideNotesRoot(string path, string notesRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(notesRoot);

        try
        {
            string root = Path.GetFullPath(notesRoot);
            string candidate = Path.GetFullPath(path);

            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string TrimSeparators(string value) => value.Trim('.', ' ', '-');

    /// <summary>按 Unicode 标量截断，避免把一个字符（如 emoji）切成半个。</summary>
    private static string TruncateByRunes(string value, int maxRunes)
    {
        // 每个标量至少占 1 个 UTF-16 单元，所以长度不超上限时必然不超标量数。
        if (value.Length <= maxRunes)
        {
            return value;
        }

        int taken = 0;
        int index = 0;

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (taken == maxRunes)
            {
                break;
            }

            index += rune.Utf16SequenceLength;
            taken++;
        }

        return value[..index];
    }
}
