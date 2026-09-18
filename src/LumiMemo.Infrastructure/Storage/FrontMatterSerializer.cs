using System.Globalization;
using System.Text;
using LumiMemo.Core.Models;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 把 <see cref="Note"/> 序列化成便签文件的<strong>原始字节</strong>（§5.2、§5.9）。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：不碰文件系统、不读时钟。落到磁盘由 <see cref="AtomicFileWriter"/> 负责（§11.2）。
/// </para>
/// <para>
/// 与 <see cref="FrontMatterParser"/> 是一对逆运算，两者的往返<strong>逐字节</strong>相等，
/// 这是 §5.9「四者原样保留」的落地形式。
/// </para>
/// </remarks>
public static class FrontMatterSerializer
{
    private static readonly ISerializer YamlSerializer =
        new SerializerBuilder().WithIndentedSequences().Build();

    /// <summary>把便签序列化成字节，含 BOM 与行尾处理。</summary>
    /// <param name="note">要写出的便签。</param>
    /// <param name="profile">
    /// 该文件在磁盘上的编码形态。由仓储按路径缓存后传入——<strong>不能在这里写死 UTF-8</strong>，
    /// 否则读进来的 GBK / UTF-16 文件会被整份改写成 UTF-8（§5.9）。
    /// </param>
    public static byte[] Serialize(Note note, NoteEncodingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(profile);

        string lineBreak = note.LineEnding == LineEnding.Lf ? "\n" : "\r\n";
        byte[] body = profile.Encoding.GetBytes(BuildText(note, lineBreak));

        if (profile.Preamble.Length == 0)
        {
            return body;
        }

        var bytes = new byte[profile.Preamble.Length + body.Length];
        profile.Preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, profile.Preamble.Length);
        return bytes;
    }

    /// <summary>拼出文件的完整文本。</summary>
    private static string BuildText(Note note, string lineBreak)
    {
        var builder = new StringBuilder();

        builder.Append(FrontMatterParser.Delimiter).Append(lineBreak);
        builder.Append(BuildFrontMatter(note, lineBreak));
        builder.Append(lineBreak);
        builder.Append(FrontMatterParser.Delimiter);

        // 结束分隔符之后的结构（§5.2）。FrontMatterTail.None 表示分隔符原本就在
        // 文件末尾，此时正文必然为空，直接接上即可。万一模型被构造成
        // 「None + 非空正文」（解析器不会这么做），补一个换行——否则正文会和
        // 分隔符挤在同一行，写出一个坏文件，而这是写用户数据的路径，宁可多一个换行。
        switch (note.FrontMatterTail)
        {
            case FrontMatterTail.None when note.Content.Length == 0:
                break;

            case FrontMatterTail.LineBreakAndBlankLine:
                builder.Append(lineBreak).Append(lineBreak);
                break;

            default:
                builder.Append(lineBreak);
                break;
        }

        builder.Append(note.Content);
        return builder.ToString();
    }

    /// <summary>
    /// 生成 Front Matter 这一段 YAML 文本（不含两端的 <c>---</c>）。
    /// </summary>
    /// <remarks>
    /// 已知字段的顺序固定为 <c>id, color, createdAt, updatedAt, tags</c>（§5.2），
    /// 便于 diff；未知字段追加在它们之后，彼此保持原有相对顺序（§5.3）。
    /// 未知字段不插回原来的位置：那会破坏「固定顺序」这条规则，而相对顺序才是
    /// 用户真正在意的东西（git diff 不抖动）。
    /// </remarks>
    private static string BuildFrontMatter(Note note, string lineBreak)
    {
        var map = new YamlMappingNode
        {
            { Scalar("id"), Scalar(note.Id.ToString("D")) },
            { Scalar("color"), Scalar(StorageName(note.Color)) },
            { Scalar("createdAt"), Scalar(FormatTimestamp(note.CreatedAt)) },
            { Scalar("updatedAt"), Scalar(FormatTimestamp(note.UpdatedAt)) },
        };

        // 空 tags 省略整个键，不写空数组（§5.2）。
        if (note.Tags.Count > 0)
        {
            var sequence = new YamlSequenceNode();
            foreach (string tag in note.Tags)
            {
                sequence.Add(Scalar(tag));
            }

            map.Add(Scalar("tags"), sequence);
        }

        foreach (KeyValuePair<string, object?> pair in note.UnknownFrontMatterKeys)
        {
            map.Add(Scalar(pair.Key), ToNode(pair.Value));
        }

        string yaml = YamlSerializer.Serialize(map);

        // 序列化器用的是平台默认换行，且结尾会带一个换行。这里把整段规整成本文件
        // 自己的行尾风格，并去掉末尾换行——末尾那个换行属于 §5.2 的结构，
        // 由 BuildText 那边按 FrontMatterTail 决定，不归这里管。
        yaml = yaml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');

        return lineBreak == "\n" ? yaml : yaml.Replace("\n", lineBreak, StringComparison.Ordinal);
    }

    private static YamlScalarNode Scalar(string value) => new(value);

    /// <summary>
    /// 把模型里的未知键取值还原成 YAML 节点。
    /// </summary>
    /// <remarks>
    /// 解析器放进来的是 <see cref="YamlNode"/> 本身（见 <see cref="FrontMatterParser"/>），
    /// 这里直接原样写回，顺序、结构、标量样式都不变。其余类型走字符串兜底：
    /// 这是写用户数据的路径，宁可写出一个朴素的值，也不能在这里抛异常。
    /// </remarks>
    private static YamlNode ToNode(object? value) => value switch
    {
        YamlNode node => node,
        null => Scalar(string.Empty),
        _ => Scalar(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// 颜色在 Front Matter 里的小写形式（§5.3）。
    /// </summary>
    /// <remarks>
    /// 用显式映射而不是 <c>ToString().ToLowerInvariant()</c>：这份字符串是
    /// <strong>落盘格式</strong>，值得被明确写出来，而不是从枚举名推导。加颜色时
    /// 这个 <c>switch</c> 会编译不过，正好提醒改这里，以及改文档里那张表。
    /// </remarks>
    private static string StorageName(NoteColor color) => color switch
    {
        NoteColor.Yellow => "yellow",
        NoteColor.Pink => "pink",
        NoteColor.Blue => "blue",
        NoteColor.Green => "green",
        NoteColor.Purple => "purple",
        NoteColor.Orange => "orange",
        NoteColor.Gray => "gray",
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, "未知的便签颜色。"),
    };
}
