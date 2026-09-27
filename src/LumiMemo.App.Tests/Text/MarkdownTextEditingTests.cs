using LumiMemo.App.Text;
using Xunit;

namespace LumiMemo.App.Tests.Text;

public sealed class MarkdownTextEditingTests
{
    [Theory]
    [InlineData("文字", 0, 2, "**文字**", 2, 2)]
    [InlineData("**文字**", 2, 2, "文字", 0, 2)]
    [InlineData("**文字**", 0, 6, "文字", 0, 2)]
    public void ToggleInline_能添加和移除标记(
        string source,
        int start,
        int length,
        string expected,
        int expectedStart,
        int expectedLength)
    {
        MarkdownTextEdit result = MarkdownTextEditing.ToggleInline(source, start, length, "**", "**");

        Assert.Equal(expected, result.Text);
        Assert.Equal(expectedStart, result.SelectionStart);
        Assert.Equal(expectedLength, result.SelectionLength);
    }

    [Fact]
    public void ToggleInline_没有选区时把光标放在标记中间()
    {
        MarkdownTextEdit result = MarkdownTextEditing.ToggleInline("前后", 1, 0, "_", "_");

        Assert.Equal("前__后", result.Text);
        Assert.Equal(2, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void ToggleLinePrefix_能把多行项目转换成待办()
    {
        const string source = "  - 第一项\r\n  * 第二项";

        MarkdownTextEdit result = MarkdownTextEditing.ToggleLinePrefix(source, 0, source.Length, "- [ ] ");

        Assert.Equal("  - [ ] 第一项\r\n  - [ ] 第二项", result.Text);
        Assert.Equal(0, result.SelectionStart);
        Assert.Equal(result.Text.Length, result.SelectionLength);
    }

    [Fact]
    public void ToggleLinePrefix_再次使用同一格式会移除前缀()
    {
        const string source = "- [ ] 第一项\n- [ ] 第二项";

        MarkdownTextEdit result = MarkdownTextEditing.ToggleLinePrefix(source, 0, source.Length, "- [ ] ");

        Assert.Equal("第一项\n第二项", result.Text);
    }

    [Fact]
    public void ToggleLinePrefix_空行会插入可继续输入的前缀()
    {
        MarkdownTextEdit result = MarkdownTextEditing.ToggleLinePrefix(string.Empty, 0, 0, "- [ ] ");

        Assert.Equal("- [ ] ", result.Text);
        Assert.Equal(6, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }
}
