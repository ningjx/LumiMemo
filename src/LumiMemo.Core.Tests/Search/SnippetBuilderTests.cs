using LumiMemo.Core.Search;
using Xunit;

namespace LumiMemo.Core.Tests.Search;

/// <summary>
/// <see cref="SnippetBuilder"/> 的摘要裁剪与命中切片（§12.3）。
/// </summary>
public sealed class SnippetBuilderTests
{
    [Fact]
    public void 短文本_整段返回且无省略号()
    {
        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build("会议记录写完了", ["记录"]);

        Assert.Equal("会议记录写完了", Concat(segments));
        Assert.DoesNotContain(segments, static s => s.Text == SnippetBuilder.Ellipsis);
    }

    [Fact]
    public void 命中在中间_前后各留上下文_两端省略号()
    {
        var text = new string('前', 200) + "关键词" + new string('后', 200);

        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(
            text, ["关键词"], maxLength: 80, leadContext: 20);

        Assert.Equal(SnippetBuilder.Ellipsis, segments[0].Text);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[^1].Text);

        int matchIndex = segments.ToList().FindIndex(static s => s.IsMatch);
        Assert.Equal("关键词", segments[matchIndex].Text);

        // 命中点前一段正好是 leadContext 个字符的上下文。
        Assert.Equal(20, segments[matchIndex - 1].Text.Length);
    }

    [Fact]
    public void 命中在尾部_窗口往回补足且结尾完整()
    {
        var text = new string('填', 300) + "目标词";

        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(
            text, ["目标词"], maxLength: 60);

        Assert.Equal(SnippetBuilder.Ellipsis, segments[0].Text);
        Assert.True(segments[^1].IsMatch);
        Assert.Equal("目标词", segments[^1].Text);
    }

    [Fact]
    public void 多处命中_都切成命中段()
    {
        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(
            "文档 中间 文档 结尾", ["文档"]);

        Assert.Equal(2, segments.Count(static s => s.IsMatch));
    }

    [Fact]
    public void 多关键词_都高亮()
    {
        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(
            "会议和记录都在这里", ["会议", "记录"]);

        Assert.Contains(segments, static s => s.IsMatch && s.Text == "会议");
        Assert.Contains(segments, static s => s.IsMatch && s.Text == "记录");
    }

    [Fact]
    public void 大小写不敏感_且保留原文大小写()
    {
        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(
            "用 Docker Compose 起的服务", ["docker"]);

        SnippetSegment match = Assert.Single(segments, static s => s.IsMatch);
        Assert.Equal("Docker", match.Text);
    }

    [Fact]
    public void 无命中_从开头截断_没有高亮段()
    {
        var text = new string('字', 300);

        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build(text, ["找不到"]);

        Assert.DoesNotContain(segments, static s => s.IsMatch);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[^1].Text);

        // 120 个正文 + 一个尾部省略号。
        Assert.Equal(SnippetBuilder.DefaultMaxLength + 1, Concat(segments).Length);
    }

    [Fact]
    public void 没有查询词_等于开头预览()
    {
        IReadOnlyList<SnippetSegment> segments = SnippetBuilder.Build("开头就在这里", []);

        Assert.Equal("开头就在这里", Concat(segments));
    }

    [Fact]
    public void 空文本或空白关键词_不炸()
    {
        Assert.Empty(SnippetBuilder.Build(null, ["词"]));
        Assert.Empty(SnippetBuilder.Build(string.Empty, ["词"]));
        Assert.Equal("正文在", Concat(SnippetBuilder.Build("正文在", [""])));
    }

    private static string Concat(IReadOnlyList<SnippetSegment> segments) =>
        string.Concat(segments.Select(static segment => segment.Text));
}
