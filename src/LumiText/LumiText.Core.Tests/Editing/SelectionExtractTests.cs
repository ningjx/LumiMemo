using LumiText.Core.Documents;
using LumiText.Core.Editing;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 选区提取单测（剪贴板复制/剪切的 Core 底座）：ExtractSelection / GetSelectionPlainText。
/// </summary>
public sealed class SelectionExtractTests
{
    private static EditorCore CoreWith(params Block[] blocks) => new(new Document(blocks));

    [Fact]
    public void ExtractSelection_Collapsed_ReturnsNull()
    {
        var core = CoreWith(new ParagraphBlock("hello"));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        Assert.Null(core.ExtractSelection());
    }

    [Fact]
    public void ExtractSelection_WithinBlock_SlicesRuns()
    {
        var core = CoreWith(new ParagraphBlock([
            new TextRun("普通"),
            new TextRun("加粗", new InlineStyle(Bold: true)),
            new TextRun("尾巴"),
        ]));
        // 选中「通加粗尾」（跨三个 run）
        core.SetSelection(new TextRange(new TextPosition(0, 1), new TextPosition(0, 5)));
        var frag = core.ExtractSelection();
        Assert.NotNull(frag);
        var p = Assert.IsType<ParagraphBlock>(frag.Blocks[0]);
        Assert.Equal("通加粗尾", p.PlainText);
        Assert.True(p.Runs[1].Style!.Bold); // 中间加粗 run 保留
    }

    [Fact]
    public void ExtractSelection_AcrossBlocks_TrimsEnds()
    {
        var core = CoreWith(
            new ParagraphBlock("第一段"),
            new ParagraphBlock("第二段"),
            new ParagraphBlock("第三段"));
        // 从第一段第 1 字符到第三段第 2 字符
        core.SetSelection(new TextRange(new TextPosition(0, 1), new TextPosition(2, 2)));
        var frag = core.ExtractSelection();
        Assert.NotNull(frag);
        Assert.Equal(3, frag.Blocks.Count);
        Assert.Equal("一段", ((ParagraphBlock)frag.Blocks[0]).PlainText);
        Assert.Equal("第二段", ((ParagraphBlock)frag.Blocks[1]).PlainText);
        Assert.Equal("第三", ((ParagraphBlock)frag.Blocks[2]).PlainText);
    }

    [Fact]
    public void GetSelectionPlainText_TodoCarriesPrefix()
    {
        var core = CoreWith(new TodoBlock("任务", @checked: true));
        core.SetSelection(new TextRange(new TextPosition(0, 0), new TextPosition(0, 2)));
        Assert.Equal("☑ 任务", core.GetSelectionPlainText());
    }

    [Fact]
    public void GetSelectionPlainText_MultilineJoinsWithNewline()
    {
        var core = CoreWith(
            new ParagraphBlock("甲"),
            new ParagraphBlock("乙"));
        core.SetSelection(new TextRange(new TextPosition(0, 0), new TextPosition(1, 1)));
        Assert.Equal("甲\n乙", core.GetSelectionPlainText());
    }
}
