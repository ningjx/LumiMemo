using System.Text;

namespace LumiMemo.Core.Services;

/// <summary>
/// 标签的规范化、去重与拆分规则（§5.8）。纯函数，无状态。
/// </summary>
/// <remarks>
/// <para>
/// §5.8 说规范化「在写入时执行一次，之后按规范形式存储与比较」。本类在<strong>读</strong>的时候
/// 也用它（解析 Front Matter 时），为的是让 <c>Note.Tags</c> 里永远是规范形式——
/// 否则「展示」「比较」「写回」三处都得各自记着再规范化一遍，漏掉一处就是同一个标签被当成两个。
/// </para>
/// <para>
/// 规则本身<strong>只有这一份实现</strong>：解析 Front Matter 与管理器的标签编辑对话框都走这里。
/// 两处各写一份的话，「编辑完写回、再读回来标签变了」这种缺陷只会在特定输入下出现，极难查。
/// </para>
/// <para>
/// 长度上限（<see cref="MaxLength"/>）<strong>这里不执行</strong>：§5.8 对它的要求是
/// 「拒绝并提示」，那是<strong>输入校验</strong>。读文件时套用它等于把用户已有的长标签直接丢掉，
/// 属于破坏数据。调用方拿 <see cref="Split"/> 的结果自己判。
/// </para>
/// </remarks>
public static class TagRules
{
    /// <summary>单个标签的长度上限（§5.8）。超出的标签由调用方拒绝并提示用户。</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// 用户输入里用来分隔标签的字符。
    /// </summary>
    /// <remarks>
    /// <strong>空格不在其中</strong>，这是刻意的：§5.8 规定标签内部的空格要换成 <c>-</c>
    /// （<c>"to read"</c> → <c>"to-read"</c>），若按空格拆分，用户打「to read」会得到两个标签，
    /// 与那条规则直接打架。中文顿号与全角逗号一并收进来，否则「工作，紧急」会变成一个标签——
    /// 中文输入法下打出全角标点是默认行为，不是用户写错了。
    /// </remarks>
    private static readonly char[] Separators = [',', '，', '、', ';', '；', '\n'];

    /// <summary>按 §5.8 规范化一个标签。</summary>
    /// <remarks>
    /// 顺序是「去首尾空白 → 剥 <c>#</c> → 内部空白换 <c>-</c>」。剥 <c>#</c> 之后要再
    /// <c>Trim</c> 一次，否则 <c>"# work"</c> 会留下一个前导空格，接着被换成前导 <c>-</c>。
    /// </remarks>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string value = raw.Trim();

        if (value.StartsWith('#'))
        {
            value = value[1..].Trim();
        }

        foreach (char c in value)
        {
            // 用一个显式的循环找空白而不是 value.Contains(' ')：制表符、全角空格
            // 也算内部空白，只认半角空格的话 "a\tb" 会被原样放过，而它照样是个非法标签。
            if (char.IsWhiteSpace(c))
            {
                return CollapseWhitespace(value);
            }
        }

        return value;
    }

    /// <summary>把内部的空白折成一个 <c>-</c>（§5.8：不允许内部空白）。</summary>
    /// <remarks>
    /// 连着几段空白只出一个 <c>-</c>；而且<strong>这一整段空白后面紧跟用户自己写的
    /// <c>-</c> 时也不补</strong>——<c>"a - b"</c> 该是 <c>a-b</c>，
    /// 不是 <c>a--b</c>（用户打的是「词 横线 词」，不是「词 横线 横线 词」）。
    /// 用户自己写出来的连续 <c>-</c> 则原样保留：那是标签本身的内容。
    /// </remarks>
    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool lastWasDash = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
                lastWasDash = c == '-';

                continue;
            }

            int next = i;
            while (next < value.Length && char.IsWhiteSpace(value[next]))
            {
                next++;
            }

            if (!lastWasDash && (next >= value.Length || value[next] != '-'))
            {
                builder.Append('-');
                lastWasDash = true;
            }

            // 整段空白一次跳过，别让后面那几个字符各自再补一个 -。
            i = next - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 规范化之后加进 <paramref name="tags"/>。
    /// </summary>
    /// <returns>真的加进去了返回 <see langword="true"/>；空标签或重复标签返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 重复的判据是<strong>忽略大小写</strong>，但保留首次出现的写法（§5.8 的原话是
    /// 「<c>Work</c> 和 <c>work</c> 视为同一个标签，存储时以首次出现的写法为准」）。
    /// 所以这里只能「先查再加」，不能交给 <c>Distinct</c> 之类的默认比较器——那会按字节序
    /// 挑一个留下，用户写的 <c>Work</c> 可能被判成重复而以 <c>work</c> 落盘。
    /// </remarks>
    public static bool TryAdd(List<string> tags, string raw)
    {
        ArgumentNullException.ThrowIfNull(tags);

        string tag = Normalize(raw);

        if (tag.Length == 0)
        {
            return false;
        }

        foreach (string existing in tags)
        {
            if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        tags.Add(tag);

        return true;
    }

    /// <summary>
    /// 把用户在标签编辑框里打的一整行拆成标签。
    /// </summary>
    /// <remarks>
    /// 已经规范化、去空、忽略大小写去重；<strong>但不做长度校验</strong>——
    /// 超过 <see cref="MaxLength"/> 的标签照样返回，由调用方决定怎么拒绝（见本类的说明）。
    /// </remarks>
    public static List<string> Split(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tags = new List<string>();

        foreach (string piece in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            _ = TryAdd(tags, piece);
        }

        return tags;
    }
}
