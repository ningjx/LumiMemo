using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 行内字号比命令（"把选中的文字放大成标题那么大"）：只作用于选中文字、其余行内样式原样保留；
/// 区间内**全部已是该比例**时再点一次＝取消（回到段落字号）。
/// </summary>
/// <remarks>
/// 布局与渲染侧早就吃这个字段（<c>Win2DTextMeasurer</c> 按 <c>base × ratio</c> 设字号），
/// 这里测的是"怎么把它写进文档"。
/// </remarks>
public sealed class InlineFontSizeRatioTests
{
    private static readonly float H1 = HeadingBlock.SizeRatioOf(1);

    private static EditorState StateWith(params Block[] blocks) =>
        EditorState.Initial(new Document(blocks));

    private static TextRange Range(int block, int start, int end) =>
        new(new TextPosition(block, start), new TextPosition(block, end));

    [Fact]
    public void 字号比取自标题字号()
    {
        // 22/18/16 ÷ 正文 14——块级标题与行内放大同源，改标题字号不会两边不一致
        Assert.Equal(22f / 14f, HeadingBlock.SizeRatioOf(1), 4);
        Assert.Equal(18f / 14f, HeadingBlock.SizeRatioOf(2), 4);
        Assert.Equal(16f / 14f, HeadingBlock.SizeRatioOf(3), 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => HeadingBlock.SizeRatioOf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadingBlock.SizeRatioOf(4));
    }

    [Fact]
    public void 只放大选中的那一段()
    {
        var state = StateWith(new ParagraphBlock("abcdef"));
        var after = new SetInlineFontSizeRatioCommand(Range(0, 2, 4), H1).Apply(state);

        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Null(p.Runs[0].Style?.FontSizeRatio);                    // "ab"
        Assert.Equal(H1, p.Runs[1].Style!.FontSizeRatio!.Value, 4);     // "cd"
        Assert.Null(p.Runs[2].Style?.FontSizeRatio);                    // "ef"
        Assert.Equal("abcdef", p.PlainText);
    }

    [Fact]
    public void 全都是该比例时再点一次等于取消()
    {
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(FontSizeRatio: H1)),
        ]));
        var after = new SetInlineFontSizeRatioCommand(Range(0, 0, 2), H1).Apply(state);

        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.Null(p.Runs[0].Style?.FontSizeRatio);
    }

    [Fact]
    public void 只有一部分是该比例时不当取消()
    {
        // 前半段是 H1 字号、后半段不是：选中整段再点 H1 ＝ 整段统一成 H1（不是取消）
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(FontSizeRatio: H1)),
            new TextRun("cd"),
        ]));
        var after = new SetInlineFontSizeRatioCommand(Range(0, 0, 4), H1).Apply(state);

        var p = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        Assert.All(p.Runs, run => Assert.Equal(H1, run.Style!.FontSizeRatio!.Value, 4));
    }

    [Fact]
    public void 其余行内样式原样保留()
    {
        var color = new Color32(0xFF, 1, 2, 3);
        var background = new Color32(0x66, 4, 5, 6);
        var state = StateWith(new ParagraphBlock([
            new TextRun("ab", new InlineStyle(
                Bold: true, Italic: true, Underline: true, Strikethrough: true,
                Color: color, Background: background)),
        ]));

        var after = new SetInlineFontSizeRatioCommand(Range(0, 0, 2), H1).Apply(state);

        var style = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]).Runs[0].Style!;
        Assert.True(style.Bold);
        Assert.True(style.Italic);
        Assert.True(style.Underline);
        Assert.True(style.Strikethrough);
        Assert.Equal(color, style.Color);
        Assert.Equal(background, style.Background);
        Assert.Equal(H1, style.FontSizeRatio!.Value, 4);
    }

    [Fact]
    public void 跨块选区两块都生效()
    {
        var state = StateWith(new ParagraphBlock("ab"), new ParagraphBlock("cd"));
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(1, 1));

        var after = new SetInlineFontSizeRatioCommand(range, H1).Apply(state);

        var first = Assert.IsType<ParagraphBlock>(after.Document.Blocks[0]);
        var second = Assert.IsType<ParagraphBlock>(after.Document.Blocks[1]);
        Assert.Equal(H1, first.Runs[^1].Style!.FontSizeRatio!.Value, 4);  // 首块的 "b"
        Assert.Equal(H1, second.Runs[0].Style!.FontSizeRatio!.Value, 4);  // 次块的 "c"
    }

    [Fact]
    public void 光标折叠时什么都不做()
    {
        var state = StateWith(new ParagraphBlock("ab"));

        Assert.Same(state, new SetInlineFontSizeRatioCommand(Range(0, 1, 1), H1).Apply(state));
    }

    [Fact]
    public void 本来就是段落字号时清除不产生变化()
    {
        // 「没变就不进历史」：否则撤销栈里会多出一堆空操作
        var state = StateWith(new ParagraphBlock("ab"));

        Assert.Same(state, new SetInlineFontSizeRatioCommand(Range(0, 0, 2), null).Apply(state));
    }
}
