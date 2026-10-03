using LumiText.Core.Documents;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// 环绕排版引擎测试矩阵（设计文档 phase0-spike-design.md §3.4 的 T1–T10）。
/// 假字体约定：字宽 10、行高 20；内容宽 100 → 全宽行 10 字符。
/// </summary>
public sealed class FlowLayoutEngineTests
{
    private const float W = 100f;

    private static LayoutResult Layout(string text, params FloatObject[] floats)
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        return engine.Layout(new[] { ParagraphBlock.FromText(text) }, floats, W);
    }

    private static FloatObject Left(int id, float x, float y, float w, float h) =>
        new(id, new LayoutRect(x, y, w, h), FloatSide.Left);

    // T1：无浮动 → 逐行全宽
    [Fact]
    public void T1_NoFloat_FullWidthLines()
    {
        using var result = Layout(FakeTextMeasurer.Text(25));

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(new[] { 10, 10, 5 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.All(result.Lines, l => Assert.Equal(0f, l.X));
        Assert.Equal(new[] { 0f, 20f, 40f }, result.Lines.Select(l => l.Y).ToArray());
        Assert.Equal(60f, result.TotalHeight);
    }

    // T2：左浮图（高 3 行）→ 前 3 行缩进图右缘，第 4 行恢复全宽
    [Fact]
    public void T2_LeftFloat_IndentedThenFullWidth()
    {
        using var result = Layout(FakeTextMeasurer.Text(25), Left(1, 0, 0, 30, 60));

        Assert.Equal(4, result.Lines.Count);
        Assert.Equal(new[] { 7, 7, 7, 4 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.Equal(26.4f, result.Lines[0].X);   // 排除区右缘 = 图右缘 − 视觉内缩 3.6
        Assert.Equal(26.4f, result.Lines[2].X);
        Assert.Equal(0f, result.Lines[3].X);       // 越过图片底部恢复全宽
        Assert.Equal(60f, result.Lines[3].Y);
    }

    // T3：右浮图 → 行右端截断于图左缘，越过后恢复
    [Fact]
    public void T3_RightFloat_ClippedThenFullWidth()
    {
        var right = new FloatObject(1, new LayoutRect(60, 0, 40, 60), FloatSide.Right);
        using var result = Layout(FakeTextMeasurer.Text(25), right);

        Assert.Equal(4, result.Lines.Count);
        Assert.Equal(new[] { 6, 6, 6, 7 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.All(result.Lines.Take(3), l => Assert.Equal(0f, l.X));
        Assert.Equal(60f, result.Lines[3].Y);
    }

    // T4：左右双浮 → 两侧同时收窄，文字走中间段
    [Fact]
    public void T4_DoubleFloat_MiddleSegmentOnly()
    {
        var right = new FloatObject(2, new LayoutRect(70, 0, 30, 20), FloatSide.Right);
        using var result = Layout(FakeTextMeasurer.Text(8), Left(1, 0, 0, 30, 20), right);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(4, result.Lines[0].CharCount);  // 中段 [26.4,73.6] = 47.2 宽
        Assert.Equal(26.4f, result.Lines[0].X);
        Assert.Equal(4, result.Lines[1].CharCount);  // 越过双浮底部恢复全宽
        Assert.Equal(0f, result.Lines[1].X);
        Assert.Equal(20f, result.Lines[1].Y);
    }

    // T5：图片高度 < 行高 → 仅相交的行被挤占（跨带行取段交集，不留空洞）
    [Fact]
    public void T5_ShortFloat_OnlyIntersectingLineIndented()
    {
        using var result = Layout(FakeTextMeasurer.Text(20), Left(1, 0, 0, 30, 10));

        Assert.Equal(3, result.Lines.Count);
        // 行高 20 横跨带 [0,10.1) 与 [10.1,∞)，段交集 [26.4,100] → 首行缩进而不是被推空
        Assert.Equal(26.4f, result.Lines[0].X);
        Assert.Equal(7, result.Lines[0].CharCount);
        Assert.Equal(0f, result.Lines[0].Y);
        Assert.Equal(0f, result.Lines[1].X);
        Assert.Equal(10, result.Lines[1].CharCount);
        Assert.Equal(3, result.Lines[2].CharCount);
    }

    // T6：段宽 < 最小字宽 → 该段放弃，文本顺延，引擎不死循环
    [Fact]
    public void T6_TooNarrowSegment_SkippedWithoutHang()
    {
        using var result = Layout(FakeTextMeasurer.Text(10), Left(1, 0, 0, 95, 20));

        Assert.Single(result.Lines);
        Assert.Equal(0f, result.Lines[0].X);
        Assert.Equal(21.32f, result.Lines[0].Y, 2);  // 推到排除区底缘（宽 91 的段也放不下字）之下
        Assert.Equal(10, result.Lines[0].CharCount);
    }

    // T7：浮动跨段落边界 → 两个段落都被环绕
    [Fact]
    public void T7_FloatAcrossParagraphs_BothWrapped()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var paragraphs = new[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10), spaceAfter: 5f),
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
        };
        using var result = engine.Layout(paragraphs, new[] { Left(1, 0, 0, 30, 60) }, W);

        Assert.Equal(4, result.Lines.Count);
        // 段落 1（10 字符）：y=0 与 y=20 都与浮动相交（< 60）→ 缩进 7 + 3
        Assert.Equal(26.4f, result.Lines[0].X);
        Assert.Equal(26.4f, result.Lines[1].X);
        // 段落 2 起于 y=45（段落 1 两行 40 + 段后距 5），仍与浮动相交 → 首行缩进
        Assert.Equal(1, result.Lines[2].BlockIndex);
        Assert.Equal(45f, result.Lines[2].Y);
        Assert.Equal(26.4f, result.Lines[2].X);
        // y=65 越过浮动底缘（60）后恢复全宽
        Assert.Equal(0f, result.Lines[3].X);
    }

    // T8：图片从左拖到右 → 文字"飞到另一边"
    [Fact]
    public void T8_DragFloat_TextFlowsToOtherSide()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var paragraphs = new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(20)) };

        using var before = engine.Layout(paragraphs, new[] { Left(1, 0, 0, 30, 40) }, W);
        Assert.Equal(26.4f, before.Lines[0].X); // 图片在左，文字在右（排除区右缘 −3.6）

        using var after = engine.Layout(paragraphs, new[] { Left(1, 70, 0, 30, 40) }, W);
        Assert.Equal(0f, after.Lines[0].X);     // 图片在右，文字回到左
        Assert.Equal(7, after.Lines[0].CharCount); // 段 [0,70]
        Assert.Equal(0f, after.Lines[2].X);     // 越过底部后全宽
        Assert.Equal(6, after.Lines[2].CharCount); // 7 + 7 + 6 = 20
    }

    // T9：两浮动 Y 区间部分重叠 → 归一化正确，交集段取最窄
    [Fact]
    public void T9_OverlappingFloats_NarrowestSegmentWins()
    {
        var f1 = Left(1, 0, 0, 30, 40);    // [0,40)
        var f2 = Left(2, 0, 20, 40, 40);   // [20,60)
        using var result = Layout(FakeTextMeasurer.Text(20), f1, f2);

        // 带 [0,20)：排除到 26.4 → 段 [26.4,100]（7 字符）；
        // 带 [20,40)：两个浮动都排除 → 段 [36,100]（6 字符）
        Assert.Equal(7, result.Lines[0].CharCount);
        Assert.Equal(26.4f, result.Lines[0].X);
        Assert.Equal(6, result.Lines[1].CharCount);
        Assert.Equal(36f, result.Lines[1].X);
        // 带 [40,60)：f2 独占 → 段 [36,100]
        Assert.Equal(36f, result.Lines[2].X);
    }

    // T10：浮动底缘与行边界恰重合 → 无 1px 缝隙/重叠
    [Fact]
    public void T10_FloatBottomExactlyAtLineBoundary()
    {
        using var result = Layout(FakeTextMeasurer.Text(30), Left(1, 0, 0, 30, 40));

        Assert.Equal(4, result.Lines.Count);
        Assert.Equal(new[] { 7, 7, 10, 6 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.Equal(26.4f, result.Lines[0].X);
        Assert.Equal(26.4f, result.Lines[1].X);
        Assert.Equal(0f, result.Lines[2].X);     // y=40 恰为边界 → 全宽
        Assert.Equal(40f, result.Lines[2].Y);
    }

    // margin 外扩排除区
    [Fact]
    public void Margin_ExpandsExclusionZone()
    {
        var withMargin = new FloatObject(1, new LayoutRect(0, 0, 30, 20), FloatSide.Left, Margin: 10f);
        using var result = Layout(FakeTextMeasurer.Text(10), withMargin);

        Assert.Equal(36.4f, result.Lines[0].X);  // 26.4（排除区右缘）+ 10 margin
        Assert.Equal(6, result.Lines[0].CharCount);
    }

    // 视觉缓冲留给文字用（Phase 3 打磨第二版）：包络与视觉矩形之间的 4dip 不计入"图片占用"，
    // 差一点点放不下的行会贴到看得见的图片边上，而不是绕到另一侧
    [Fact]
    public void VisualBuffer_LetsLineReachImageEdge()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(20)) },
            new[] { Left(1, 197, 0, 60, 40) }, 300f);

        // 排除区右缘 = 197 + 4（缓冲）→ 20 字（200dip）恰好放得下，整行留在图片左侧
        Assert.Single(result.Lines);
        Assert.Equal(0f, result.Lines[0].X);
        Assert.Equal(20, result.Lines[0].CharCount);
    }

    // 空段落占一个空行高度
    [Fact]
    public void EmptyParagraph_OccupiesOneLineHeight()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var paragraphs = new[]
        {
            ParagraphBlock.FromText(FakeTextMeasurer.Text(10)),
            ParagraphBlock.FromText(string.Empty),
            ParagraphBlock.FromText(FakeTextMeasurer.Text(5)),
        };
        using var result = engine.Layout(paragraphs, Array.Empty<FloatObject>(), W);

        // 空段落也产出行盒（M8 起：空块补零字符占位行，空 bullet/todo 的标记与光标落点需要）
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(1, result.Lines[1].BlockIndex);
        Assert.Equal(0, result.Lines[1].CharCount);
        Assert.Equal(20f, result.Lines[1].Y);
        Assert.Equal(40f, result.Lines[2].Y);  // 20（行）+ 20（空行）
        Assert.Equal(2, result.Lines[2].BlockIndex);
    }

    // 命中测试：行盒反查（M2 起为字符级）
    [Fact]
    public void HitTest_FindsLineAndFloat()
    {
        using var result = Layout(FakeTextMeasurer.Text(25), Left(7, 0, 0, 30, 60));

        var hit = result.HitTest(50, 5);
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        // M2 字符级：点 (50,5) 落在首行第 5 个字符（等宽 10dip），落尾标记由中点判定
        Assert.True(hit.CharIndex >= 0);

        var floatHit = result.FloatAt(15, 15);
        Assert.NotNull(floatHit);
        Assert.Equal(7, floatHit.Id);

        Assert.Null(result.FloatAt(95, 95));
    }

    // 窗口收窄：右缘溢出的浮动被拉回内容框，环绕按归一化后的位置生效
    [Fact]
    public void FloatBeyondRightEdge_IsPulledBackIntoContent()
    {
        var right = new FloatObject(1, new LayoutRect(80, 0, 30, 40), FloatSide.Right);
        using var result = Layout(FakeTextMeasurer.Text(20), right);

        Assert.Equal(70f, result.Floats[0].Rect.X);      // 右缘贴内容右缘（100 − 30）
        Assert.Equal(100f, result.Floats[0].Rect.Right);
        Assert.Equal(new[] { 7, 7, 6 }, result.Lines.Select(l => l.CharCount).ToArray());
        Assert.Equal(0f, result.Lines[0].X);
        Assert.Equal(70f, result.Lines[0].Bounds.Right); // 行右端截断于图片左缘
        Assert.Equal(0f, result.Lines[2].X);             // 越过图片底部恢复全宽
        Assert.Equal(6, result.Lines[2].CharCount);
    }

    // 矩形宽于内容区：只贴左缘，不缩放
    [Fact]
    public void FloatWiderThanContent_SticksToLeftEdge()
    {
        var wide = new FloatObject(1, new LayoutRect(10, 0, 150, 20), FloatSide.Left);
        using var result = Layout(FakeTextMeasurer.Text(10), wide);

        Assert.Equal(0f, result.Floats[0].Rect.X);
        Assert.Equal(150f, result.Floats[0].Rect.Right);
        Assert.Equal(21.93f, result.Lines[0].Y, 2);  // 推到排除区之下（宽图视觉底缘 21.93）
    }

    // 左/上溢出：钳到原点
    [Fact]
    public void NegativeOffsets_ClampToOrigin()
    {
        var f = new FloatObject(1, new LayoutRect(-20, -30, 30, 40), FloatSide.Left);
        using var result = Layout(FakeTextMeasurer.Text(10), f);

        Assert.Equal(0f, result.Floats[0].Rect.X);
        Assert.Equal(0f, result.Floats[0].Rect.Y);
    }

    // 归一化只作用于本次排版：内容变宽后浮动回到作者原位
    [Fact]
    public void PlacementIsTransient_WideningRestoresAuthoredPosition()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var paragraphs = new[] { ParagraphBlock.FromText(FakeTextMeasurer.Text(10)) };
        var right = new FloatObject(1, new LayoutRect(280, 0, 30, 40), FloatSide.Right);

        using (var narrow = engine.Layout(paragraphs, new[] { right }, 200f))
        {
            Assert.Equal(170f, narrow.Floats[0].Rect.X);
        }
        using (var wide = engine.Layout(paragraphs, new[] { right }, 400f))
        {
            Assert.Equal(280f, wide.Floats[0].Rect.X);
        }
    }

    // bullet 段落：文本整体右移缩进、行盒挂 BulletText 标记（圆点由渲染层画在缩进区）
    [Fact]
    public void BulletParagraph_IndentedAndMarked()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            new Document([new ParagraphBlock(FakeTextMeasurer.Text(10), isBullet: true)]), W);

        Assert.Equal(2, result.Lines.Count);     // 段宽 = 100 − 16 = 84，假字体 10/字 → 8+2 折两行
        var first = result.Lines[0];
        Assert.Equal(PlacedLineKind.BulletText, first.Kind);
        Assert.True(first.IsBlockStart);
        Assert.Equal(16f, first.X);              // ParagraphBlock.LeftIndent
        Assert.Equal(8, first.CharCount);
        Assert.Equal(80f, first.Width);
        Assert.False(result.Lines[1].IsBlockStart); // 圆点只画首行
    }

    // 空 bullet 段落也产出行盒（否则圆点画不出、视觉上空行 bullet 消失）
    [Fact]
    public void EmptyBulletParagraph_PlaceholderLineKeepsMarker()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(
            new Document([new ParagraphBlock("", isBullet: true)]), W);

        var line = Assert.Single(result.Lines);
        Assert.Equal(PlacedLineKind.BulletText, line.Kind);
        Assert.True(line.IsBlockStart);
        Assert.Equal(0, line.CharCount);
        Assert.Equal(FakeTextMeasurer.LineHeight, result.TotalHeight);
    }

    // 空 todo 段同样产出行盒（既有行为只推进高度，复选框原本画不出）
    [Fact]
    public void EmptyTodoBlock_PlaceholderLineKeepsCheckbox()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        using var result = engine.Layout(new Document([new TodoBlock("")]), W);

        var line = Assert.Single(result.Lines);
        Assert.Equal(PlacedLineKind.TodoText, line.Kind);
        Assert.True(line.IsBlockStart);
        Assert.Equal(0, line.CharCount);
    }
}
