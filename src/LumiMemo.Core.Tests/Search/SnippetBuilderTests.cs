using LumiMemo.Core.Search;
using Xunit;

namespace LumiMemo.Core.Tests.Search;

/// <summary>
/// <see cref="SnippetBuilder"/> 的单元测试：卡片副标题上的摘要与命中高亮（§12.3）。
/// </summary>
public sealed class SnippetBuilderTests
{
    // ================= 截取窗口 =================

    [Fact]
    public void 命中在中间时前后都截断并加省略号()
    {
        var text = new string('a', 100) + "needle" + new string('b', 100);

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(5, segments.Count);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[0].Text);
        Assert.Equal(new string('a', SnippetBuilder.ContextLength), segments[1].Text);
        Assert.Equal("needle", segments[2].Text);
        Assert.True(segments[2].IsMatch);
        Assert.Equal(new string('b', SnippetBuilder.ContextLength), segments[3].Text);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[4].Text);
    }

    [Fact]
    public void 命中落在开头附近时不加前导省略号()
    {
        // §12.3 第 5 条：前面本来就没有内容被截掉，画个省略号会造成"前面还有"的错觉。
        var text = "needle" + new string('b', 200);

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(3, segments.Count);
        Assert.Equal("needle", segments[0].Text);
        Assert.True(segments[0].IsMatch);
        Assert.Equal(new string('b', SnippetBuilder.ContextLength), segments[1].Text);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[2].Text);
    }

    [Fact]
    public void 命中落在结尾附近时不加尾随省略号()
    {
        var text = new string('a', 200) + "needle";

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(3, segments.Count);
        Assert.Equal(SnippetBuilder.Ellipsis, segments[0].Text);
        Assert.Equal(new string('a', SnippetBuilder.ContextLength), segments[1].Text);
        Assert.Equal("needle", segments[2].Text);
        Assert.True(segments[2].IsMatch);
    }

    [Fact]
    public void 命中位置正好等于上下文长度时也不算被截断()
    {
        // 边界：start 恰好落在 0。差一个字符就会多出一个前导省略号，
        // 所以这个用例钉的是那个 "<" 而不是 "<="。
        var text = new string('a', SnippetBuilder.ContextLength) + "needle" + new string('b', 200);

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(new string('a', SnippetBuilder.ContextLength), segments[0].Text);
        Assert.False(segments[0].IsMatch);
    }

    [Fact]
    public void 上下文长度只有四十个字符()
    {
        var text = new string('a', 500) + "needle" + new string('b', 500);

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(40, SnippetBuilder.ContextLength);
        Assert.Equal(40, segments[1].Text.Length);
        Assert.Equal(40, segments[3].Text.Length);
    }

    // ================= 高亮段 =================

    [Fact]
    public void 高亮段取自原文_保留原来的大小写()
    {
        // 高亮的文本必须是原文那一段，不能拿查询词顶替——否则用户按小写搜，
        // 摘要里显示的就成了小写，看起来像原文被改了。
        var text = new string('-', 60) + "DOCKER" + new string('-', 60);

        var segments = SnippetBuilder.Build(text, "docker");

        var matched = Assert.Single(segments, static s => s.IsMatch);
        Assert.Equal("DOCKER", matched.Text);
    }

    [Fact]
    public void 只摘第一处命中()
    {
        // 每处都摘会把一行摘要撑成好几行（§12.3）。
        var text = "needle" + new string('x', 100) + "needle";

        var segments = SnippetBuilder.Build(text, "needle");

        Assert.Equal(1, segments.Count(static s => s.IsMatch));
        Assert.Equal("needle", segments[0].Text);
    }

    [Fact]
    public void 查询词两端空白会被裁掉()
    {
        var text = new string('a', 100) + "needle" + new string('b', 100);

        var segments = SnippetBuilder.Build(text, "  needle  ");

        Assert.Equal("needle", Assert.Single(segments, static s => s.IsMatch).Text);
    }

    [Fact]
    public void 匹配忽略大小写()
    {
        var segments = SnippetBuilder.Build("Docker 的部署笔记", "DOCKER");

        Assert.Equal("Docker", Assert.Single(segments, static s => s.IsMatch).Text);
    }

    // ================= 换行 =================

    [Fact]
    public void 换行被换成空格_切片位置不受影响()
    {
        // 替换是一对一的，长度不变，所以算出来的下标对原文同样成立。
        // 若哪天改成"整行拼接再裁"，这条会立刻红。
        var text = string.Concat(Enumerable.Repeat("一二三\n四五六\n", 10)) + "目标词" + new string('z', 200);

        var segments = SnippetBuilder.Build(text, "目标词");

        var joined = string.Concat(segments.Select(static s => s.Text));

        Assert.Equal(-1, joined.IndexOf('\n'));
        Assert.Equal(-1, joined.IndexOf('\r'));
        Assert.Equal("目标词", Assert.Single(segments, static s => s.IsMatch).Text);

        // 窗口前段正好落满 5 个周期（每周期 8 个字符、含两个换行）→ 10 个空格。
        Assert.Equal(40, segments[1].Text.Length);
        Assert.Equal(10, segments[1].Text.Count(static c => c == ' '));
    }

    [Fact]
    public void 回车换行也只换成一个空格()
    {
        var text = new string('a', 100) + "一\r\n二" + new string('b', 100);

        var segments = SnippetBuilder.Build(text, "一");

        var joined = string.Concat(segments.Select(static s => s.Text));

        Assert.Equal(-1, joined.IndexOf('\r'));
        Assert.Equal(-1, joined.IndexOf('\n'));
    }

    // ================= 不生成摘要的情形 =================

    [Fact]
    public void 正文里找不到查询词时返回空列表()
    {
        Assert.Empty(SnippetBuilder.Build("这里没有那个词", "找不到"));
    }

    [Fact]
    public void 空查询返回空列表()
    {
        Assert.Empty(SnippetBuilder.Build("正文内容", ""));
        Assert.Empty(SnippetBuilder.Build("正文内容", "   "));
        Assert.Empty(SnippetBuilder.Build("正文内容", null));
    }

    [Fact]
    public void 空正文返回空列表()
    {
        Assert.Empty(SnippetBuilder.Build(null, "查询"));
        Assert.Empty(SnippetBuilder.Build("", "查询"));
    }

    // ================= 整段命中 =================

    [Fact]
    public void 正文整篇就是查询词时只返回一段()
    {
        var segments = SnippetBuilder.Build("needle", "needle");

        Assert.Equal("needle", Assert.Single(segments).Text);
        Assert.True(segments[0].IsMatch);
    }
}
