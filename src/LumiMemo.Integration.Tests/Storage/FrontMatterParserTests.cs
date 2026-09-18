using System.Text;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="FrontMatterParser"/> 的测试（§5.2、§5.9、§5.10）。
/// </summary>
/// <remarks>
/// 解析器是纯函数，这些用例<strong>不碰文件系统</strong>，跑得很快。
/// 放在集成测试工程里只是因为被测类型住在 Infrastructure——
/// 「哪些测试快、哪些测试慢」的分界是「要不要打磁盘」，不是「住在哪个工程」。
/// </remarks>
public sealed class FrontMatterParserTests
{
    private const string GuidText = "6f1d0a2e-1111-2222-3333-444455556666";

    // ---- Front Matter 切分（§5.2） ----

    [Fact]
    public void 没有FrontMatter的文件_整份文件都是正文()
    {
        ParsedNoteFile parsed = Parse("# 标题\r\n\r\n正文\r\n");

        Assert.Equal("# 标题\r\n\r\n正文\r\n", parsed.Result.Content);
        Assert.Null(parsed.Result.Id);
        Assert.False(parsed.FrontMatterUnparsable);
        Assert.Empty(parsed.Result.ParseIssues);
    }

    [Fact]
    public void 未闭合的FrontMatter_整份文件都是正文且不算读不懂()
    {
        // §5.10：只有开头的 --- 而没有结束的 ---，视为无 Front Matter。
        // 这里必须「不算读不懂」——那些用户写的内容一个字都不该被改写，
        // 也不能触发备份，因为压根没坏。
        const string text = "---\r\nid: 不是合法guid\r\n# 标题\r\n";

        ParsedNoteFile parsed = Parse(text);

        Assert.Equal(text, parsed.Result.Content);
        Assert.Null(parsed.Result.Id);
        Assert.False(parsed.FrontMatterUnparsable);
    }

    [Fact]
    public void 标准FrontMatter_正文里不含那个约定空行()
    {
        ParsedNoteFile parsed = Parse(
            "---\r\n"
            + $"id: {GuidText}\r\n"
            + "color: blue\r\n"
            + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
            + "updatedAt: 2026-09-19T11:00:00+08:00\r\n"
            + "tags:\r\n"
            + "  - docker\r\n"
            + "  - to-read\r\n"
            + "---\r\n"
            + "\r\n"
            + "# 标题\r\n");

        // Front Matter 与正文之间那个空行是格式约定，不是用户的正文。
        // 若把它算进 Content，便签窗口打开时就会凭空多出一个空的首行。
        Assert.Equal("# 标题\r\n", parsed.Result.Content);
        Assert.Equal(FrontMatterTail.LineBreakAndBlankLine, parsed.Result.FrontMatterTail);

        Assert.Equal(Guid.Parse(GuidText), parsed.Result.Id);
        Assert.Equal(NoteColor.Blue, parsed.Result.Color);
        Assert.Equal(["docker", "to-read"], parsed.Result.Tags);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8)),
            parsed.Result.CreatedAt);
    }

    [Fact]
    public void 结束分隔符后面没有空行_正文紧跟着开始()
    {
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\n---\r\n# 标题\r\n");

        Assert.Equal("# 标题\r\n", parsed.Result.Content);
        Assert.Equal(FrontMatterTail.LineBreakOnly, parsed.Result.FrontMatterTail);
    }

    [Fact]
    public void 结束分隔符正好在文件末尾_正文为空且形态为None()
    {
        // 这一形态必须与「分隔符后面有一个换行」区分开，否则写回时会凭空补一个换行，
        // 用户的 git diff 就出现了自己没有的改动（§5.9）。
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\n---");

        Assert.Equal(string.Empty, parsed.Result.Content);
        Assert.Equal(FrontMatterTail.None, parsed.Result.FrontMatterTail);
    }

    [Fact]
    public void 结束分隔符后面的空行不止一个_多出来的属于正文()
    {
        // §5.2 只规定「一个」空行。用户自己多敲的空行是他的内容，不能吃掉。
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\n---\r\n\r\n\r\n正文\r\n");

        Assert.Equal("\r\n正文\r\n", parsed.Result.Content);
        Assert.Equal(FrontMatterTail.LineBreakAndBlankLine, parsed.Result.FrontMatterTail);
    }

    [Fact]
    public void 只有FrontMatter_正文为空()
    {
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\n---\r\n\r\n");

        Assert.Equal(string.Empty, parsed.Result.Content);
        Assert.Equal(FrontMatterTail.LineBreakAndBlankLine, parsed.Result.FrontMatterTail);
    }

    // ---- 降级（§5.10） ----

    [Fact]
    public void Yaml语法错误_不抛异常且只记录位置不记录原文()
    {
        ParsedNoteFile parsed = Parse("---\r\nid: [没有闭合\r\n---\r\n\r\n正文\r\n");

        Assert.True(parsed.FrontMatterUnparsable);
        Assert.Null(parsed.Result.Id);
        Assert.Equal("正文\r\n", parsed.Result.Content);

        NoteParseIssue issue = Assert.Single(parsed.Result.ParseIssues);
        Assert.Equal(NoteParseIssueKind.InvalidYaml, issue.Kind);

        // §19.5：YamlDotNet 的异常消息里会内嵌出错的那几行原文，那是用户的笔记内容，
        // 绝不能进日志。这里钉住我们只取了异常类型与行号。
        Assert.NotNull(issue.Detail);
        Assert.DoesNotContain("没有闭合", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontMatter不是键值映射_算读不懂()
    {
        // 若不算「读不懂」，补写 id 时就会按「未知字段零个」走替换流程，
        // 用户写的这两行会被静默抹掉。
        ParsedNoteFile parsed = Parse("---\r\n- 甲\r\n- 乙\r\n---\r\n\r\n正文\r\n");

        Assert.True(parsed.FrontMatterUnparsable);
        Assert.Equal("正文\r\n", parsed.Result.Content);
    }

    [Fact]
    public void 空文件_当作一张空便签()
    {
        ParsedNoteFile parsed = FrontMatterParser.Parse([]);

        Assert.Equal(string.Empty, parsed.Result.Content);
        Assert.Null(parsed.Result.Id);
        Assert.False(parsed.FrontMatterUnparsable);
        Assert.Empty(parsed.Result.ParseIssues);
    }

    [Fact]
    public void 颜色写成数字_不认作枚举()
    {
        // Enum.TryParse 会把 "3" 认成 Gray，那就把用户写错的东西悄悄当成了合法值。
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\ncolor: 3\r\n---\r\n\r\n正文\r\n");

        Assert.Null(parsed.Result.Color);
    }

    [Fact]
    public void 时间戳无法解析_留空由仓储层退回文件系统时间()
    {
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\ncreatedAt: 昨天\r\n---\r\n\r\n正文\r\n");

        Assert.Null(parsed.Result.CreatedAt);
    }

    [Fact]
    public void 标签写成字符串_按单个标签处理()
    {
        ParsedNoteFile parsed = Parse($"---\r\nid: {GuidText}\r\ntags: to read\r\n---\r\n\r\n正文\r\n");

        Assert.Equal(["to-read"], parsed.Result.Tags);
        Assert.Contains(parsed.Result.ParseIssues, issue => issue.Kind == NoteParseIssueKind.InvalidYaml);
    }

    [Fact]
    public void 标签规范化_去井号_空白转连字符_去重不分大小写()
    {
        ParsedNoteFile parsed = Parse(
            "---\r\n"
            + $"id: {GuidText}\r\n"
            + "tags:\r\n"
            + "  - \"#工作\"\r\n"
            + "  - \"to   read\"\r\n"
            + "  - Work\r\n"
            + "  - work\r\n"
            + "  - \"\"\r\n"
            + "---\r\n"
            + "\r\n正文\r\n");

        // 空标签丢弃；Work/work 视为同一个，保留先出现的那个写法。
        Assert.Equal(["工作", "to-read", "Work"], parsed.Result.Tags);
    }

    [Fact]
    public void 文件过大_仍能加载但记一条提示()
    {
        var builder = new StringBuilder("# 大文件\r\n");
        builder.Append('x', FrontMatterParser.FullTextSearchSizeLimit + 1);

        ParsedNoteFile parsed = Parse(builder.ToString());

        NoteParseIssue issue = Assert.Single(parsed.Result.ParseIssues);
        Assert.Equal(NoteParseIssueKind.FileTooLarge, issue.Kind);
    }

    // ---- 未知字段（§5.3） ----

    [Fact]
    public void 未知字段原样保留且保持原有顺序()
    {
        ParsedNoteFile parsed = Parse(
            "---\r\n"
            + "zebra: 1\r\n"
            + $"id: {GuidText}\r\n"
            + "alpha: two\r\n"
            + "aliases:\r\n"
            + "  - 甲\r\n"
            + "  - 乙\r\n"
            + "---\r\n"
            + "\r\n正文\r\n");

        Assert.Equal(["zebra", "alpha", "aliases"], parsed.Result.UnknownFrontMatterKeys.Select(pair => pair.Key));

        // 存的是 YamlNode 本身，不是反序列化后的 Dictionary——后者会丢掉顺序。
        var aliases = Assert.IsType<YamlSequenceNode>(parsed.Result.UnknownFrontMatterKeys[2].Value);
        Assert.Equal(["甲", "乙"], aliases.Children.Cast<YamlScalarNode>().Select(item => item.Value));
    }

    [Fact]
    public void 未知字段的嵌套映射_顺序与结构都保留()
    {
        ParsedNoteFile parsed = Parse(
            "---\r\n"
            + $"id: {GuidText}\r\n"
            + "cssclasses:\r\n"
            + "  - wide\r\n"
            + "dataview:\r\n"
            + "  甲: 一\r\n"
            + "  乙: 二\r\n"
            + "---\r\n"
            + "\r\n正文\r\n");

        var dataview = Assert.IsType<YamlMappingNode>(parsed.Result.UnknownFrontMatterKeys[1].Value);
        Assert.Equal(["甲", "乙"], dataview.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
    }

    // ---- 编码与行尾（§5.9） ----

    [Fact]
    public void 带BOM的UTF8_剥掉BOM解析但记住它有BOM()
    {
        byte[] raw = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"---\r\nid: {GuidText}\r\n---\r\n\r\n正文\r\n")];

        ParsedNoteFile parsed = FrontMatterParser.Parse(raw);

        Assert.True(parsed.Result.HadBom);
        Assert.True(parsed.Encoding.HasBom);
        Assert.Equal("正文\r\n", parsed.Result.Content);

        // BOM 必须已经被裁掉：它若留在正文里，便签第一行会多一个看不见的字符。
        Assert.DoesNotContain('﻿', parsed.Result.Content);
    }

    [Fact]
    public void 不是合法UTF8_回退到指定代码页并标记编码异常()
    {
        Encoding gbk = CodePagesEncodingProvider.Instance.GetEncoding(936)!;
        NoteEncodingProfile ansi = NoteEncodingProfile.AnsiWith(gbk);
        byte[] raw = gbk.GetBytes("---\r\ncolor: blue\r\n---\r\n\r\n中文正文\r\n");

        ParsedNoteFile parsed = FrontMatterParser.Parse(raw, ansi);

        Assert.Same(ansi, parsed.Encoding);
        Assert.Equal("中文正文\r\n", parsed.Result.Content);
        Assert.Equal(NoteColor.Blue, parsed.Result.Color);
        Assert.Contains(parsed.Result.ParseIssues, issue => issue.Kind == NoteParseIssueKind.InvalidEncoding);
    }

    [Fact]
    public void UTF16小端_识别BOM并标记编码异常()
    {
        byte[] body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("---\r\ncolor: pink\r\n---\r\n\r\n正文\r\n"))
            .ToArray();

        ParsedNoteFile parsed = FrontMatterParser.Parse(body);

        Assert.Equal(NoteColor.Pink, parsed.Result.Color);
        Assert.Equal("正文\r\n", parsed.Result.Content);
        Assert.Contains(parsed.Result.ParseIssues, issue => issue.Kind == NoteParseIssueKind.InvalidEncoding);
    }

    [Fact]
    public void 全是LF的文件_行尾探测为LF()
    {
        ParsedNoteFile parsed = Parse("---\nid: " + GuidText + "\n---\n\n正文\n");

        Assert.Equal(LineEnding.Lf, parsed.Result.LineEnding);
    }

    [Fact]
    public void 一行换行都没有的文件_行尾取默认的CRLF()
    {
        // 没有换行就没有「原有风格」可以保留，于是采用 §5.9 给新建便签定的默认组合。
        ParsedNoteFile parsed = Parse("只有一行没有换行");

        Assert.Equal(LineEnding.CrLf, parsed.Result.LineEnding);
    }

    [Fact]
    public void 混合行尾的文件_只要有CRLF就按CRLF记()
    {
        ParsedNoteFile parsed = Parse("---\nid: " + GuidText + "\n---\r\n\r\n正文\r\n");

        Assert.Equal(LineEnding.CrLf, parsed.Result.LineEnding);
    }

    private static ParsedNoteFile Parse(string text) => FrontMatterParser.Parse(Encoding.UTF8.GetBytes(text));
}
