using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// BlockTextOps 单测：run 序列的文本替换/样式变换/切片——命令层的底座。
/// </summary>
public sealed class BlockTextOpsTests
{
    [Fact]
    public void ReplaceText_InsertIntoSingleRun_KeepsStyle()
    {
        var block = new ParagraphBlock([new TextRun("hello", new InlineStyle(Bold: true))]);
        var result = BlockTextOps.ReplaceText(block, 2, 0, "XY");
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("heXYllo", p.PlainText);
        Assert.Single(p.Runs); // 同样式合并
        Assert.True(p.Runs[0].Style!.Bold);
    }

    [Fact]
    public void ReplaceText_InsertAcrossStyleBoundary_InheritsPreviousCharStyle()
    {
        var block = new ParagraphBlock([
            new TextRun("normal"),
            new TextRun("bold", new InlineStyle(Bold: true)),
        ]);
        // 在两个 run 的接缝处（偏移 6）插入——继承前字符（normal）的样式
        var result = BlockTextOps.ReplaceText(block, 6, 0, "X");
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("normalXbold", p.PlainText);
        Assert.Equal(2, p.Runs.Count);
        Assert.Equal("normalX", p.Runs[0].Text);
        Assert.Null(p.Runs[0].Style);
        Assert.Equal("bold", p.Runs[1].Text);
        Assert.True(p.Runs[1].Style!.Bold);
    }

    [Fact]
    public void ReplaceText_DeleteAcrossStyleBoundary_MergesNeighbors()
    {
        var block = new ParagraphBlock([
            new TextRun("ab"),
            new TextRun("XY", new InlineStyle(Bold: true)),
            new TextRun("cd"),
        ]);
        // 删掉 "bXYc"（从 1 开始 4 个字符），剩 "ad"
        var result = BlockTextOps.ReplaceText(block, 1, 4, string.Empty);
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("ad", p.PlainText);
        Assert.Single(p.Runs);
        Assert.Null(p.Runs[0].Style);
    }

    [Fact]
    public void ReplaceText_InsertAtBlockStart_InsertsAtStartNotEnd()
    {
        // 回归：块首（偏移 0）插入曾掉进"末尾兜底"——前面没有 run 的右缘可挂，
        // 首 run 又被判成"编辑区间之后"，字符于是跑到块尾（实机表现：光标在行首打字，字接在末尾）。
        var block = new ParagraphBlock("123");
        var result = BlockTextOps.ReplaceText(block, 0, 0, "4");
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("4123", p.PlainText);
        Assert.Single(p.Runs); // 同样式合并
    }

    [Fact]
    public void ReplaceText_InsertAtFirstRun_StartInheritsFirstRunStyle()
    {
        var block = new ParagraphBlock([
            new TextRun("123", new InlineStyle(Bold: true)),
            new TextRun("456"),
        ]);
        var result = BlockTextOps.ReplaceText(block, 0, 0, "X");
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("X123456", p.PlainText);
        Assert.Equal("X123", p.Runs[0].Text);
        Assert.True(p.Runs[0].Style!.Bold); // 块首插入继承首 run 样式
    }

    [Fact]
    public void ReplaceText_EmptyBlock_InsertsPlainRun()
    {
        var block = new ParagraphBlock([]);
        var result = BlockTextOps.ReplaceText(block, 0, 0, "new");
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal("new", p.PlainText);
        Assert.Single(p.Runs);
    }

    [Fact]
    public void ReplaceText_NewlineRejected()
    {
        var block = new ParagraphBlock("a");
        Assert.Throws<ArgumentException>(() => BlockTextOps.ReplaceText(block, 0, 0, "x\ny"));
    }

    [Fact]
    public void TransformInlineStyle_PartialRange_SplitsRuns()
    {
        var block = new ParagraphBlock([new TextRun("abcdef")]);
        var result = BlockTextOps.TransformInlineStyle(block, 2, 2,
            s => (s ?? new InlineStyle()) with { Bold = true });
        var p = Assert.IsType<ParagraphBlock>(result);
        Assert.Equal(3, p.Runs.Count);
        Assert.Equal("ab", p.Runs[0].Text);
        Assert.Equal("cd", p.Runs[1].Text);
        Assert.True(p.Runs[1].Style!.Bold);
        Assert.Equal("ef", p.Runs[2].Text);
        Assert.Null(p.Runs[2].Style);
    }

    [Fact]
    public void SliceRuns_ExactRange_ReturnsFragments()
    {
        var block = new ParagraphBlock([
            new TextRun("abc"),
            new TextRun("DEF", new InlineStyle(Italic: true)),
        ]);
        var slice = BlockTextOps.SliceRuns(block, 2, 3); // "cDE"
        Assert.Equal(2, slice.Count);
        Assert.Equal("c", slice[0].Text);
        Assert.Equal("DE", slice[1].Text);
        Assert.True(slice[1].Style!.Italic);
    }
}
