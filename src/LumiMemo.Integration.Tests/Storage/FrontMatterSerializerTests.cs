using System.Text;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="FrontMatterSerializer"/> 的测试（§5.2、§5.3、§5.9）。
/// </summary>
/// <remarks>
/// 与 <see cref="FrontMatterParserTests"/> 一样是纯函数测试，不碰文件系统。
/// </remarks>
public sealed class FrontMatterSerializerTests
{
    private static readonly Guid SampleId = Guid.Parse("6f1d0a2e-1111-2222-3333-444455556666");

    private static readonly DateTimeOffset SampleCreated =
        new(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset SampleUpdated =
        new(2026, 9, 19, 11, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 往返_逐字节相等()
    {
        // §5.9 的核心不变量：读进来再写回去，一个字节都不能变。
        // 这是整个存储层最该被钉死的一条。
        const string original =
            "---\r\n"
            + "id: 6f1d0a2e-1111-2222-3333-444455556666\r\n"
            + "color: blue\r\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
            + "updatedAt: 2026-09-19T11:30:00+08:00\r\n"
            + "tags:\r\n"
            + "  - docker\r\n"
            + "  - to-read\r\n"
            + "---\r\n"
            + "\r\n"
            + "# 标题\r\n"
            + "\r\n"
            + "正文\r\n";

        byte[] roundTripped = SerializeRoundTrip(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, Encoding.UTF8.GetString(roundTripped));
    }

    [Fact]
    public void 往返_LF文件的行尾保持不变()
    {
        const string original =
            "---\n"
            + "id: 6f1d0a2e-1111-2222-3333-444455556666\n"
            + "color: yellow\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\n"
            + "updatedAt: 2026-09-19T11:30:00+08:00\n"
            + "---\n"
            + "\n"
            + "正文\n";

        byte[] roundTripped = SerializeRoundTrip(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, Encoding.UTF8.GetString(roundTripped));
    }

    [Fact]
    public void 往返_带BOM的文件仍然带BOM()
    {
        byte[] original =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes($"---\r\nid: {SampleId:D}\r\ncolor: yellow\r\ncreatedAt: 2026-09-19T10:00:00+08:00\r\nupdatedAt: 2026-09-19T11:30:00+08:00\r\n---\r\n\r\n正文\r\n"),
        ];

        byte[] roundTripped = SerializeRoundTrip(original);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void 往返_原文没有结尾换行_写回也不补()
    {
        const string original =
            "---\r\n"
            + "id: 6f1d0a2e-1111-2222-3333-444455556666\r\n"
            + "color: yellow\r\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
            + "updatedAt: 2026-09-19T11:30:00+08:00\r\n"
            + "---\r\n"
            + "\r\n"
            + "正文没有结尾换行";

        byte[] roundTripped = SerializeRoundTrip(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, Encoding.UTF8.GetString(roundTripped));
    }

    [Fact]
    public void 往返_结束分隔符后没有空行的文件_也不会被补上空行()
    {
        const string original =
            "---\r\n"
            + "id: 6f1d0a2e-1111-2222-3333-444455556666\r\n"
            + "color: yellow\r\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
            + "updatedAt: 2026-09-19T11:30:00+08:00\r\n"
            + "---\r\n"
            + "正文\r\n";

        byte[] roundTripped = SerializeRoundTrip(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, Encoding.UTF8.GetString(roundTripped));
    }

    [Fact]
    public void 往返_原始编码是GBK的_写回还是GBK()
    {
        Encoding gbk = CodePagesEncodingProvider.Instance.GetEncoding(936)!;
        NoteEncodingProfile profile = NoteEncodingProfile.AnsiWith(gbk);

        const string original =
            "---\r\n"
            + "id: 6f1d0a2e-1111-2222-3333-444455556666\r\n"
            + "color: yellow\r\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
            + "updatedAt: 2026-09-19T11:30:00+08:00\r\n"
            + "---\r\n"
            + "\r\n"
            + "中文正文\r\n";

        byte[] raw = gbk.GetBytes(original);
        ParsedNoteFile parsed = FrontMatterParser.Parse(raw, profile);
        Note note = ToNote(parsed, raw);

        byte[] written = FrontMatterSerializer.Serialize(note, parsed.Encoding);

        // 若这里按 UTF-8 写回，用户整篇中文笔记就变成乱码了（§5.9）。
        Assert.Equal(raw, written);
    }

    [Fact]
    public void 未知字段写在已知字段之后_且保持原有相对顺序()
    {
        Note note = NewNote();
        note.UnknownFrontMatterKeys.Add(new KeyValuePair<string, object?>("zebra", new YamlScalarNode("1")));
        note.UnknownFrontMatterKeys.Add(new KeyValuePair<string, object?>("alpha", new YamlScalarNode("two")));

        string text = Text(FrontMatterSerializer.Serialize(note, NoteEncodingProfile.Utf8));

        int idIndex = text.IndexOf("id:", StringComparison.Ordinal);
        int zebraIndex = text.IndexOf("zebra:", StringComparison.Ordinal);
        int alphaIndex = text.IndexOf("alpha:", StringComparison.Ordinal);

        Assert.True(idIndex < zebraIndex, "已知字段必须排在未知字段前面（§5.2 的固定顺序）");
        Assert.True(zebraIndex < alphaIndex, "未知字段之间必须保持原有顺序（§5.3）");
    }

    [Fact]
    public void 没有标签时_整个tags键都不写()
    {
        Note note = NewNote();

        string text = Text(FrontMatterSerializer.Serialize(note, NoteEncodingProfile.Utf8));

        Assert.DoesNotContain("tags", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 标签用两格缩进的块序列()
    {
        Note note = NewNote();
        note.Tags = ["docker", "to-read"];

        string text = Text(FrontMatterSerializer.Serialize(note, NoteEncodingProfile.Utf8));

        Assert.Contains("tags:\r\n  - docker\r\n  - to-read\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 新建便签_写成带空行的标准形态()
    {
        string text = Text(FrontMatterSerializer.Serialize(NewNote(), NoteEncodingProfile.Utf8));

        Assert.Contains("---\r\n\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("---\r\n\r\n正文\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 颜色写成小写英文()
    {
        Note note = NewNote();
        note.Color = NoteColor.Purple;

        string text = Text(FrontMatterSerializer.Serialize(note, NoteEncodingProfile.Utf8));

        Assert.Contains("color: purple", text, StringComparison.Ordinal);
    }

    private static byte[] SerializeRoundTrip(byte[] raw)
    {
        ParsedNoteFile parsed = FrontMatterParser.Parse(raw);

        return FrontMatterSerializer.Serialize(ToNote(parsed, raw), parsed.Encoding);
    }

    /// <summary>把解析结果原样搬进 <see cref="Note"/>，模拟「读进来什么都没改就写回去」。</summary>
    private static Note ToNote(ParsedNoteFile parsed, byte[] raw) => new()
    {
        Id = parsed.Result.Id ?? SampleId,
        FilePath = "不参与序列化.md",
        Content = parsed.Result.Content,
        Color = parsed.Result.Color ?? NoteColor.Yellow,
        Tags = [.. parsed.Result.Tags],
        CreatedAt = parsed.Result.CreatedAt ?? SampleCreated,
        UpdatedAt = parsed.Result.UpdatedAt ?? SampleUpdated,
        LineEnding = parsed.Result.LineEnding,
        HadBom = parsed.Result.HadBom,
        FrontMatterTail = parsed.Result.FrontMatterTail,
        UnknownFrontMatterKeys = [.. parsed.Result.UnknownFrontMatterKeys],
        ParseIssues = [.. parsed.Result.ParseIssues],
    };

    private static Note NewNote() => new()
    {
        Id = SampleId,
        FilePath = "不参与序列化.md",
        Content = "正文\r\n",
        Color = NoteColor.Yellow,
        CreatedAt = SampleCreated,
        UpdatedAt = SampleUpdated,
    };

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
