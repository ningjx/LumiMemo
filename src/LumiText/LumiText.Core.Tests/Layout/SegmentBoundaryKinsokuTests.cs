using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Layout;

/// <summary>
/// 段边界放宽避头尾（Phase 3 打磨）：图片两侧的分段里，"下一行行首"其实是同一视觉行的另一段，
/// 断行器为「收尾标点不可行首」退掉的那个字要补回来，标点让给下一段；正文换行照旧守避头尾。
/// 假字体（字宽 10dip、行高 20dip）按真实栈同一规则实现避头尾，否则这条逻辑在单测里触发不到。
/// </summary>
public sealed class SegmentBoundaryKinsokuTests
{
    private const float W = 100f;

    [Fact]
    public void SegmentBoundary_RelaxesKinsoku_MovesPunctuationToNextSegment()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText("abc，defghijklmnop") },
            // 排除区 = [30 + 4（视觉内缩）, 70 − 4] ：左段按宽度放得下 3 字，但第 4 个字是逗号
            new[] { new FloatObject(1, new LayoutRect(30, 0, 40, 60), FloatSide.Left) }, W);

        var row = result.Lines.Where(l => l.Y == 0f).ToList();
        Assert.Equal(2, row.Count);
        // 左边补回被避头尾退掉的字："abc"（而不是"ab"）
        Assert.Equal(0f, row[0].X);
        Assert.Equal(3, row[0].CharCount);
        // 逗号让给图片右侧那一段：右段从逗号开始
        Assert.Equal(66f, row[1].X);
        Assert.Equal(3, row[1].CharStart);
        Assert.Equal(3, row[1].CharCount);
    }

    [Fact]
    public void RowLastSegment_StillHonorsKinsoku()
    {
        // 右段是本行最后一段（文字接不到同行的其他段）→ 不放宽：
        // 「def」里 d 后是逗号，断行器照旧退字（右段首行 = 2 字 "de"），逗号不起行
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText("abcdef，ghijklmnop") },
            new[] { new FloatObject(1, new LayoutRect(30, 0, 40, 60), FloatSide.Left) }, W);

        var row = result.Lines.Where(l => l.Y == 0f).ToList();
        Assert.Equal(2, row.Count);
        Assert.Equal(3, row[0].CharCount);       // 左段：3 字，边界上是 'd'（不是标点）→ 无补字
        Assert.Equal(3, row[1].CharStart);       // 右段从 'd' 起
        Assert.Equal(2, row[1].CharCount);       // 'd' 被和逗号一起退下来 → "de"
    }

    [Fact]
    public void NormalWrap_StillHonorsKinsoku()
    {
        // 没有浮动、单段成行：正文换行照旧守避头尾——断点前退一格，逗号不起行
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(new Document([new ParagraphBlock("abc，defghijklmnop")]), 34f);

        Assert.Equal(2, result.Lines[0].CharCount);       // "ab"（"c" 被和逗号一起推下去）
        Assert.Equal(2, result.Lines[1].CharStart);       // 下一行从 "c" 起，逗号仍在行中
        Assert.Equal('c', "abc，defghijklmnop"[result.Lines[1].CharStart]);
    }
}
