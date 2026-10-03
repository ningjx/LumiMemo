using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 粘贴插图命令（Phase 2 设计 §5.1/§0.2.6）：插入一个浮动锚定当前字符的 <see cref="ImageBlock"/>，
/// 并把图片字节注册进 <see cref="Document.Images"/>。
/// O4 降级形态（2026-10-03 拍板）：内嵌图片不做行内 <c>SetInlineObject</c>，
/// 降级为「浮动图片锚定到当前字符」——锚定语义随 2026-10-03 字符级补丁（<see cref="FloatAnchor"/>）。
/// </summary>
/// <remarks>
/// 图片作为独立块插入到「锚点所在块之后」（不打断当前段落文本流）；
/// 锚点指向插入位置前一个文本块的对应字符，使图片随该字符所在行浮动。
/// 执行后光标落在插入点原处（插图不移动文本光标）。
/// </remarks>
public sealed record InsertImageCommand(
    string ImageId,
    byte[] Data,
    string Mime,
    float Width,
    float Height,
    FloatSide Side = FloatSide.Right) : IEditCommand
{
    public string Kind => "insert-image";

    public EditorState Apply(EditorState state)
    {
        ArgumentNullException.ThrowIfNull(Data);
        if (string.IsNullOrEmpty(ImageId))
        {
            throw new ArgumentException("ImageId 不能为空。", nameof(ImageId));
        }
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "图片尺寸必须为正。");
        }

        var document = state.Document;
        var caret = state.Selection.Active;
        var blocks = document.Blocks;
        if (blocks.Count == 0)
        {
            // 空文档：补一个空段落承载锚点，图片插到其后
            blocks = [new ParagraphBlock("")];
            document = document with { Blocks = blocks };
            caret = new TextPosition(0, 0);
        }

        int anchorBlock = Math.Clamp(caret.BlockIndex, 0, blocks.Count - 1);
        // 锚点字符：当前块的 caret 偏移；锚到非文本块时顺延到前一个文本块
        int anchorChar = caret.CharIndex;
        if (!BlockTextOps.IsTextBlock(blocks[anchorBlock]))
        {
            (anchorBlock, anchorChar) = FindPreviousTextBlock(blocks, anchorBlock);
        }

        var imageBlock = new ImageBlock(ImageId, Width, Height,
            new FloatPlacement(Side, Margin: 4f, Anchor: new FloatAnchor(anchorBlock, anchorChar)));

        // 图片块插到锚点块之后
        var newBlocks = new List<Block>(blocks.Count + 1);
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks.Add(blocks[i]);
            if (i == anchorBlock)
            {
                newBlocks.Add(imageBlock);
            }
        }

        // 注册图片字节
        var images = document.Images is null
            ? new List<ImageResource>()
            : new List<ImageResource>(document.Images);
        images.RemoveAll(r => r.Id == ImageId); // 幂等：同 id 覆盖
        images.Add(new ImageResource(ImageId, Mime, Data));

        // 浮动锚点维护（Phase 3 M4）：图片块插在锚点块之后，其后所有锚点整块后移
        var remapped = FloatAnchors.RemapAll(newBlocks, anchor =>
            anchor.BlockIndex > anchorBlock
                ? anchor with { BlockIndex = anchor.BlockIndex + 1 }
                : anchor);

        return new EditorState(
            document with { Blocks = remapped, Images = images },
            state.Selection);
    }

    private static (int block, int charIndex) FindPreviousTextBlock(IReadOnlyList<Block> blocks, int from)
    {
        for (int i = from - 1; i >= 0; i--)
        {
            if (BlockTextOps.IsTextBlock(blocks[i]))
            {
                return (i, BlockTextOps.GetTextLength(blocks[i]));
            }
        }
        return (0, 0);
    }
}
