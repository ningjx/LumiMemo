using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// Enter 分块命令（Phase 2 设计 §5.1）：把 position 所在块在字符偏移处一分为二。
/// 前段保留原块型与块级属性；后段沿用同型块（HeadingBlock 分块后后半降级为 ParagraphBlock，
/// 与 Word/RichEdit 的「标题行尾 Enter 后新行为正文」语义对齐；TodoBlock 行首 Enter 退前缀的
/// 伪待办投影由上层输入法/快捷键层处理，命令层不感知）。
/// 执行后光标落在后段块首。
/// </summary>
public sealed record SplitBlockCommand(TextPosition Position) : IEditCommand
{
    public string Kind => "split-block";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        var block = blocks[Position.BlockIndex];
        if (!BlockTextOps.IsTextBlock(block))
        {
            throw new InvalidOperationException("只能在文本块内分块。");
        }

        int length = BlockTextOps.GetTextLength(block);
        if (Position.CharIndex < 0 || Position.CharIndex > length)
        {
            throw new ArgumentOutOfRangeException(nameof(Position),
                $"分块偏移 {Position.CharIndex} 超出块文本长度 {length}。");
        }

        var headRuns = BlockTextOps.SliceRuns(block, 0, Position.CharIndex);
        var tailRuns = BlockTextOps.SliceRuns(block, Position.CharIndex, length - Position.CharIndex);

        var firstHalf = BlockTextOps.WithRuns(block, headRuns);
        Block secondHalf = block switch
        {
            // 标题行尾 Enter → 新段为正文（沿用 RichEdit/Word 语义）；中段 Enter → 两段同型
            HeadingBlock h when Position.CharIndex == length =>
                new ParagraphBlock(tailRuns, null, h.SpaceAfter),
            HeadingBlock h => new HeadingBlock(tailRuns, h.Level, h.SpaceAfter),
            TodoBlock t => new TodoBlock(tailRuns, false, t.SpaceAfter), // 新行默认未勾选
            ParagraphBlock p => new ParagraphBlock(tailRuns, p.Style, p.SpaceAfter),
            _ => throw new InvalidOperationException("不支持的块型。"),
        };

        // 间距归属：前段吃掉原 SpaceAfter 归 0，原 SpaceAfter 由后段继承（分段后视觉间距不变）
        firstHalf = ResetSpaceAfter(firstHalf);

        var newBlocks = new List<Block>(blocks.Count + 1);
        for (int i = 0; i < Position.BlockIndex; i++)
        {
            newBlocks.Add(blocks[i]);
        }
        newBlocks.Add(firstHalf);
        newBlocks.Add(secondHalf);
        for (int i = Position.BlockIndex + 1; i < blocks.Count; i++)
        {
            newBlocks.Add(blocks[i]);
        }

        var caret = new TextPosition(Position.BlockIndex + 1, 0);
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            TextRange.Collapse(caret));
    }

    private static Block ResetSpaceAfter(Block block) => block switch
    {
        ParagraphBlock p => new ParagraphBlock(p.Runs, p.Style, 0f),
        HeadingBlock h => new HeadingBlock(h.Runs, h.Level, 0f),
        TodoBlock t => new TodoBlock(t.Runs, t.Checked, 0f),
        _ => block,
    };
}
