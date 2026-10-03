using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// InsertImageCommand 单测（O4 降级形态）：浮动锚定当前字符 + 图片字节注册。
/// </summary>
public sealed class InsertImageCommandTests
{
    private static readonly byte[] FakePng = [1, 2, 3, 4];

    [Fact]
    public void Apply_InsertsImageBlockAfterCaretBlock()
    {
        var core = new EditorCore(new Document([
            new ParagraphBlock("第一段"),
            new ParagraphBlock("第二段"),
        ]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 200, 100));

        Assert.Equal(3, core.Document.Blocks.Count);
        Assert.IsType<ParagraphBlock>(core.Document.Blocks[0]);
        var img = Assert.IsType<ImageBlock>(core.Document.Blocks[1]);
        Assert.Equal("img-1", img.ImageId);
        Assert.Equal(200f, img.Width);
        Assert.Equal(100f, img.Height);
        Assert.IsType<ParagraphBlock>(core.Document.Blocks[2]);
    }

    [Fact]
    public void Apply_AnchorsToCaretCharacter()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("第一段")]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 2)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 100, 100));

        var img = Assert.IsType<ImageBlock>(core.Document.Blocks[1]);
        Assert.NotNull(img.Float);
        Assert.NotNull(img.Float!.Anchor);
        Assert.Equal(0, img.Float.Anchor!.BlockIndex);
        Assert.Equal(2, img.Float.Anchor.CharIndex); // 锚定 caret 所在字符
    }

    [Fact]
    public void Apply_RegistersImageBytes()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("a")]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 1)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 50, 50));

        Assert.NotNull(core.Document.Images);
        var res = Assert.Single(core.Document.Images!);
        Assert.Equal("img-1", res.Id);
        Assert.Equal("image/png", res.Mime);
        Assert.Equal(FakePng, res.Data);
    }

    [Fact]
    public void Apply_DoesNotMoveCaret()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("abc")]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 1)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 50, 50));
        Assert.Equal(new TextPosition(0, 1), core.Selection.Active); // 插图不移动文本光标
    }

    [Fact]
    public void Apply_EmptyDocument_CreatesAnchorParagraph()
    {
        var core = new EditorCore(new Document([]));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 50, 50));
        Assert.Equal(2, core.Document.Blocks.Count);
        Assert.IsType<ParagraphBlock>(core.Document.Blocks[0]); // 补的空段落
        Assert.IsType<ImageBlock>(core.Document.Blocks[1]);
    }

    [Fact]
    public void Apply_GetFloatsProducesAnchorFloat()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("文本")]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 1)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 120, 80));
        var floats = core.Document.GetFloats();
        var f = Assert.Single(floats);
        Assert.NotNull(f.Anchor);
        Assert.Equal(120f, f.Rect.Width);
        Assert.Equal(80f, f.Rect.Height);
    }

    [Fact]
    public void Apply_UndoRemovesImage()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("文本")]));
        core.SetSelection(TextRange.Collapse(new TextPosition(0, 1)));
        core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 50, 50));
        Assert.Equal(2, core.Document.Blocks.Count);
        core.Undo();
        Assert.Single(core.Document.Blocks);
        Assert.Null(core.Document.Images);
    }

    [Fact]
    public void Apply_InvalidSize_Throws()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("a")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            core.ApplyCommand(new InsertImageCommand("img-1", FakePng, "image/png", 0, 50)));
    }
}
