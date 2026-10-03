using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// Backspace/Delete/剪切的范围删除命令（Phase 2 设计 §5.1）。
/// 单块内：删 [start, end) 文本；跨块：删首块尾部 + 中间整块 + 尾块头部，
/// 首块与尾块按「尾块 runs 拼到首块尾」合并（尾块为非文本块时整块删除，首块留头部）。
/// 执行后光标坍缩到原选区 Start。撤销由 EditorCore 快照通道处理。
/// </summary>
public sealed record DeleteRangeCommand(TextRange Range) : IEditCommand
{
    public string Kind => "delete";

    public EditorState Apply(EditorState state)
    {
        if (Range.IsCollapsed)
        {
            return state;
        }

        var start = Range.Start;
        var end = Range.End;
        var blocks = state.Document.Blocks;

        if (start.BlockIndex == end.BlockIndex)
        {
            var block = blocks[start.BlockIndex];
            int count = end.CharIndex - start.CharIndex;
            var newBlock = BlockTextOps.ReplaceText(block, start.CharIndex, count, string.Empty);
            var newBlocks = InsertTextCommand.ReplaceBlock(blocks, start.BlockIndex, newBlock);
            return new EditorState(
                state.Document with { Blocks = newBlocks },
                TextRange.Collapse(start));
        }

        var firstBlock = blocks[start.BlockIndex];
        var lastBlock = blocks[end.BlockIndex];
        bool firstIsText = BlockTextOps.IsTextBlock(firstBlock);
        bool lastIsText = BlockTextOps.IsTextBlock(lastBlock);

        var kept = new List<Block>(blocks.Count - (end.BlockIndex - start.BlockIndex));
        for (int i = 0; i < start.BlockIndex; i++)
        {
            kept.Add(blocks[i]);
        }

        if (firstIsText && lastIsText)
        {
            // 首块留头部，尾块留尾部，拼成一块（保留首块的块型与块级属性）
            var headRuns = BlockTextOps.SliceRuns(firstBlock, 0, start.CharIndex);
            var tailRuns = BlockTextOps.SliceRuns(lastBlock, end.CharIndex,
                BlockTextOps.GetTextLength(lastBlock) - end.CharIndex);
            var mergedRuns = new List<TextRun>(headRuns.Count + tailRuns.Count);
            mergedRuns.AddRange(headRuns);
            mergedRuns.AddRange(tailRuns);
            kept.Add(BlockTextOps.WithRuns(firstBlock, NormalizeRuns(mergedRuns)));
        }
        else if (firstIsText)
        {
            // 尾块非文本（Divider/Image）：首块留头部，尾块连同中间块一并删除
            var headRuns = BlockTextOps.SliceRuns(firstBlock, 0, start.CharIndex);
            kept.Add(BlockTextOps.WithRuns(firstBlock, headRuns));
        }
        else if (lastIsText)
        {
            // 首块非文本：首块连同中间块一并删除，尾块留尾部
            var tailRuns = BlockTextOps.SliceRuns(lastBlock, end.CharIndex,
                BlockTextOps.GetTextLength(lastBlock) - end.CharIndex);
            kept.Add(BlockTextOps.WithRuns(lastBlock, tailRuns));
        }
        // else：两端都非文本——首/尾连同中间块一并删除

        for (int i = end.BlockIndex + 1; i < blocks.Count; i++)
        {
            kept.Add(blocks[i]);
        }

        TextPosition caret;
        if (firstIsText)
        {
            caret = start;
        }
        else
        {
            int newIndex = Math.Min(start.BlockIndex, kept.Count - 1);
            caret = new TextPosition(Math.Max(newIndex, 0), 0);
        }

        return new EditorState(
            state.Document with { Blocks = kept },
            TextRange.Collapse(caret));
    }

    internal static IReadOnlyList<TextRun> NormalizeRuns(List<TextRun> runs)
    {
        var result = new List<TextRun>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }
            if (result.Count > 0 &&
                EqualityComparer<InlineStyle?>.Default.Equals(result[^1].Style, run.Style))
            {
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            }
            else
            {
                result.Add(run);
            }
        }
        return result;
    }
}
