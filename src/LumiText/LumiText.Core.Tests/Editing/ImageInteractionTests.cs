using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using LumiText.Core.Layout;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// Phase 3 M4：图片交互的 Core 部分——拖动改锚点/浮动侧、缩放手柄几何与提交命令。
/// </summary>
public sealed class ImageInteractionTests
{
    private static EditorState StateWith(params Block[] blocks) =>
        EditorState.Initial(new Document(blocks));

    private static ImageBlock AnchoredImage(float width = 120f, float height = 80f,
        FloatSide side = FloatSide.Right, FloatAnchor? anchor = null) =>
        new("img", width, height, new FloatPlacement(side, 4f, anchor ?? new FloatAnchor(0, 0)));

    // ---------------- MoveImageAnchorCommand ----------------

    [Fact]
    public void MoveImage_SetsAnchorAndSide()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage());
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 2), FloatSide.Left).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(new FloatAnchor(0, 2), image.Float!.Anchor);
        Assert.Equal(FloatSide.Left, image.Float.Side);
        Assert.Equal(120f, image.Width); // 尺寸不动
    }

    [Fact]
    public void MoveImage_ClearsFreePosition()
    {
        var positioned = new ImageBlock("img", 100f, 60f,
            new FloatPlacement(FloatSide.Right, 4f, Anchor: null, Position: new FloatPosition(30f, 40f)));
        var state = StateWith(new ParagraphBlock("文本"), positioned);
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 1), FloatSide.Right).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Null(image.Float!.Position);
        Assert.Equal(new FloatAnchor(0, 1), image.Float.Anchor);
    }

    [Fact]
    public void MoveImage_SameAnchorAndSide_NoChange()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage(anchor: new FloatAnchor(0, 1)));
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 1), FloatSide.Right).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    [Fact]
    public void MoveImage_SetsCharacterFollowingAnchor()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage());
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 1), FloatSide.Right,
            AnchorToChar: true).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.True(image.Float!.AnchorToChar);                  // 紧跟锚字符之后
        Assert.Equal(new FloatAnchor(0, 1), image.Float.Anchor); // 锚点本身
    }

    [Fact]
    public void MoveImage_SamePlacement_NoChange()
    {
        var placed = new ImageBlock("img", 120f, 80f,
            new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 0), null, AnchorToChar: true));
        var state = StateWith(new ParagraphBlock("文本"), placed);
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 0), FloatSide.Right,
            AnchorToChar: true).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    [Fact]
    public void MoveImage_CharacterFollowing_ClearsMargin()
    {
        // 拖动落点语义：紧跟锚字符的图片不留边距（4dip 会把锚字符所在行的尾巴挤出到图片右侧）
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage()); // 默认 margin 4
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 1), FloatSide.Right,
            AnchorToChar: true).Apply(state);
        Assert.Equal(0f, Assert.IsType<ImageBlock>(after.Document.Blocks[1]).Float!.Margin);

        // 贴缘语义（粘贴默认）保留边距
        var paste = new MoveImageAnchorCommand(1, new FloatAnchor(0, 1), FloatSide.Right).Apply(state);
        Assert.Equal(4f, Assert.IsType<ImageBlock>(paste.Document.Blocks[1]).Float!.Margin);
    }

    [Fact]
    public void AnchoredImage_AtLineEnd_KeepsWholeLineLeftOfImage()
    {
        // 末行行尾锚定：10 字符行（宽 100）应完整留在图片左侧（X=0，一行放完）
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ImageBlock("img", 60f, 40f,
                new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 10), null,
                    AnchorToChar: true)),
        };
        using var result = engine.Layout(new Document(blocks), 200f);

        // 图片 [100,160]；首行段 [0,100] 恰好容纳整行
        var firstLine = Assert.Single(result.Lines,
            l => l.BlockIndex == 0 && l.Y == 0f && l.Kind == PlacedLineKind.Text);
        Assert.Equal(10, firstLine.CharCount); // 整行文字都在图片左侧
        Assert.Equal(0f, firstLine.X);
    }

    [Fact]
    public void AnchoredImage_WithMargin_PushesLineTailRightOfImage()
    {
        // 对照：边距大于视觉缓冲（8 > 4）时，排除区左移，整行放不下 → 尾字被绕到图片右侧
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ImageBlock("img", 60f, 40f,
                new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(0, 10), null,
                    AnchorToChar: true)),
        };
        using var result = engine.Layout(new Document(blocks), 200f);

        var rowLines = result.Lines
            .Where(l => l.BlockIndex == 0 && l.Y == 0f && l.Kind == PlacedLineKind.Text)
            .ToList();
        Assert.Equal(2, rowLines.Count);          // 行被切成两段
        Assert.Equal(9, rowLines[0].CharCount);   // 左边只放得下 9 个（排除区右缘 104 − 8 边距 = 96）
        Assert.Equal(164f, rowLines[1].X);        // 尾字被推到图片右侧（右缘 156 + 8 边距）
    }

    [Fact]
    public void MoveImage_NotImageBlock_NoChange()
    {
        var state = StateWith(new ParagraphBlock("a"), new ParagraphBlock("b"));
        var after = new MoveImageAnchorCommand(1, new FloatAnchor(0, 0), FloatSide.Left).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    [Fact]
    public void MoveImage_UndoRestoresAnchor()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("文本"), AnchoredImage()]));
        core.ApplyCommand(new MoveImageAnchorCommand(1, new FloatAnchor(0, 3), FloatSide.Left));
        Assert.Equal(new FloatAnchor(0, 3), ((ImageBlock)core.Document.Blocks[1]).Float!.Anchor);

        Assert.True(core.Undo());
        Assert.Equal(new FloatAnchor(0, 0), ((ImageBlock)core.Document.Blocks[1]).Float!.Anchor);
    }

    // ---------------- ResizeImageCommand ----------------

    [Fact]
    public void ResizeImage_ChangesSize_KeepsPlacement()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage());
        var after = new ResizeImageCommand(1, 200f, 140f).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(200f, image.Width);
        Assert.Equal(140f, image.Height);
        Assert.Equal(new FloatAnchor(0, 0), image.Float!.Anchor); // 锚定/侧不动
        Assert.Equal(FloatSide.Right, image.Float.Side);
    }

    [Fact]
    public void ResizeImage_SameSize_NoChange()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage(120f, 80f));
        var after = new ResizeImageCommand(1, 120f, 80f).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    [Fact]
    public void ResizeImage_NonPositiveSize_Throws()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResizeImageCommand(1, 0f, 80f).Apply(state));
    }

    // ---------------- 手柄几何 ----------------

    private static LayoutRect Image(float x = 100f, float y = 50f, float w = 120f, float h = 80f) =>
        new(x, y, w, h);

    [Fact]
    public void HandleCenter_CornersAndEdges()
    {
        var image = Image(); // (100,50) 120×80 → 右 220、下 130
        Assert.Equal((100f, 50f), ImageResizeGeometry.HandleCenter(image, ImageHandle.TopLeft));
        Assert.Equal((160f, 50f), ImageResizeGeometry.HandleCenter(image, ImageHandle.Top));
        Assert.Equal((220f, 50f), ImageResizeGeometry.HandleCenter(image, ImageHandle.TopRight));
        Assert.Equal((220f, 90f), ImageResizeGeometry.HandleCenter(image, ImageHandle.Right));
        Assert.Equal((220f, 130f), ImageResizeGeometry.HandleCenter(image, ImageHandle.BottomRight));
        Assert.Equal((160f, 130f), ImageResizeGeometry.HandleCenter(image, ImageHandle.Bottom));
        Assert.Equal((100f, 130f), ImageResizeGeometry.HandleCenter(image, ImageHandle.BottomLeft));
        Assert.Equal((100f, 90f), ImageResizeGeometry.HandleCenter(image, ImageHandle.Left));
    }

    [Fact]
    public void HitTest_NearestHandleWithinRadius()
    {
        var image = Image();
        Assert.Equal(ImageHandle.BottomRight, ImageResizeGeometry.HitTest(image, 222f, 132f));
        Assert.Equal(ImageHandle.Top, ImageResizeGeometry.HitTest(image, 162f, 48f));
        Assert.Equal(ImageHandle.None, ImageResizeGeometry.HitTest(image, 160f, 90f)); // 图片中心
    }

    [Fact]
    public void Resize_CornerIsProportional_AndKeepsOppositeCorner()
    {
        var image = Image(0f, 0f, 100f, 50f); // 右 100、下 50
        // 拖右下角到 (200, 60)：等比取较大比例 max(2.0, 1.2) = 2.0；左上角不动
        var rect = ImageResizeGeometry.Resize(image, ImageHandle.BottomRight, 200f, 60f, 400f);
        Assert.Equal(0f, rect.X);
        Assert.Equal(0f, rect.Y);
        Assert.Equal(200f, rect.Width);
        Assert.Equal(100f, rect.Height);
    }

    [Fact]
    public void Resize_TopLeftHandle_MovesTopLeftEdge_KeepsBottomRight()
    {
        var image = Image(100f, 50f, 120f, 80f); // 右 220、下 130
        // 拖左上角到 (60, 20)：等比 max(160/120, 110/80) = 1.375 → 165×110
        var rect = ImageResizeGeometry.Resize(image, ImageHandle.TopLeft, 60f, 20f, 400f);
        Assert.Equal(220f, rect.Right);  // 右下角钉住
        Assert.Equal(130f, rect.Bottom);
        Assert.Equal(55f, rect.X);       // 220 − 165
        Assert.Equal(20f, rect.Y);       // 130 − 110
        Assert.Equal(165f, rect.Width);
        Assert.Equal(110f, rect.Height);
    }

    [Fact]
    public void Resize_LeftHandle_MovesLeftEdge_KeepsRightEdge()
    {
        var image = Image(100f, 50f, 120f, 80f);
        var rect = ImageResizeGeometry.Resize(image, ImageHandle.Left, 60f, 999f, 400f);
        Assert.Equal(60f, rect.X);       // 左缘跟指针
        Assert.Equal(220f, rect.Right);  // 右缘钉住
        Assert.Equal(160f, rect.Width);
        Assert.Equal(50f, rect.Y);       // 纵向不动
        Assert.Equal(80f, rect.Height);
    }

    [Fact]
    public void Resize_EdgeIsSingleAxis()
    {
        var image = Image(0f, 0f, 100f, 50f);
        var rectRight = ImageResizeGeometry.Resize(image, ImageHandle.Right, 150f, 999f, 400f);
        Assert.Equal(150f, rectRight.Width);
        Assert.Equal(50f, rectRight.Height); // 高度不变
        Assert.Equal(0f, rectRight.X);       // 左缘不动

        var rectBottom = ImageResizeGeometry.Resize(image, ImageHandle.Bottom, 999f, 90f, 400f);
        Assert.Equal(100f, rectBottom.Width); // 宽度不变
        Assert.Equal(90f, rectBottom.Height);
        Assert.Equal(0f, rectBottom.Y);       // 上缘不动
    }

    [Fact]
    public void Resize_ClampsToMinEdgeAndMaxWidth()
    {
        var image = Image(0f, 0f, 400f, 200f);
        // 四角拖到图片内很远：等比缩到最小边长（50）——短边先触底，长边按比例跟着
        var rectMin = ImageResizeGeometry.Resize(image, ImageHandle.BottomRight, 1f, 1f, 800f);
        Assert.Equal(100f, rectMin.Width);  // 等比：400×(50/200)=100 ≥ 50
        Assert.Equal(50f, rectMin.Height);

        // 单边手柄：下边拖到最小高度，宽度不动
        var rectShort = ImageResizeGeometry.Resize(image, ImageHandle.Bottom, 999f, 1f, 800f);
        Assert.Equal(400f, rectShort.Width);
        Assert.Equal(50f, rectShort.Height);

        // 超过内容区宽：钳到 maxWidth
        var rectMax = ImageResizeGeometry.Resize(image, ImageHandle.Right, 900f, 100f, 300f);
        Assert.Equal(300f, rectMax.Width);
        Assert.Equal(200f, rectMax.Height);
    }

    // ---------------- 缩放松手的重锚落位（左/上侧手柄，Phase 3 M4）----------------

    [Fact]
    public void ResolveAnchoredResize_KeepsBottomRight_LandsTopLeftOnAnchor()
    {
        // 10 字符行（宽 100，内容区 200）：自由矩形左缘拖到 60 → 锚到第 6 号字符的插入位置
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[] { new ParagraphBlock(FakeTextMeasurer.Text(10)) };
        using var natural = engine.Layout(new Document(blocks), 200f);

        // 原图右缘 160、下缘 60；左缘拖到 60（宽 100）
        var free = new LayoutRect(60f, 0f, 100f, 60f);
        var landed = ImageResizeGeometry.ResolveAnchoredResize(natural, free, 200f);

        Assert.NotNull(landed);
        Assert.Equal(new FloatAnchor(0, 6), landed.Value.Anchor);
        Assert.Equal(60f, landed.Value.Rect.X);       // 锚字符之后（第 6 号字符左缘 = 60）
        Assert.Equal(0f, landed.Value.Rect.Y);
        Assert.Equal(160f, landed.Value.Rect.Right);  // 右下边缘钉回自由矩形的位置
        Assert.Equal(60f, landed.Value.Rect.Bottom);
    }

    [Fact]
    public void ResolveAnchoredResize_SnapsLeftEdgeByCharacter()
    {
        // 自由矩形左缘拖到 67（第 6 号字符右半，边界 65 之后）→ 锚到插入位置 7（X=70），右缘仍钉在 160
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[] { new ParagraphBlock(FakeTextMeasurer.Text(10)) };
        using var natural = engine.Layout(new Document(blocks), 200f);

        var landed = ImageResizeGeometry.ResolveAnchoredResize(
            natural, new LayoutRect(67f, 0f, 93f, 60f), 200f);

        Assert.NotNull(landed);
        Assert.Equal(7, landed.Value.Anchor.CharIndex);
        Assert.Equal(70f, landed.Value.Rect.X);
        Assert.Equal(160f, landed.Value.Rect.Right);  // 尺寸跟着让，右下边缘不动
        Assert.Equal(90f, landed.Value.Rect.Width);
    }

    [Fact]
    public void ResizeImage_WithAnchor_MovesAnchorAndSize()
    {
        var state = StateWith(new ParagraphBlock("文本"), AnchoredImage()); // 锚 (0,0)
        var after = new ResizeImageCommand(1, 90f, 60f, new FloatAnchor(0, 1)).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(90f, image.Width);
        Assert.Equal(60f, image.Height);
        Assert.Equal(new FloatAnchor(0, 1), image.Float!.Anchor);
        Assert.True(image.Float.AnchorToChar);
        Assert.Equal(0f, image.Float.Margin);
        Assert.Null(image.Float.Position);
    }

    [Fact]
    public void ResizeImage_WithAnchor_OneUndoRestoresBoth()
    {
        var core = new EditorCore(new Document([new ParagraphBlock("文本"), AnchoredImage()]));
        core.ApplyCommand(new ResizeImageCommand(1, 90f, 60f, new FloatAnchor(0, 3)));

        var resized = (ImageBlock)core.Document.Blocks[1];
        Assert.Equal(new FloatAnchor(0, 3), resized.Float!.Anchor);
        Assert.Equal(90f, resized.Width);

        Assert.True(core.Undo()); // 尺寸与锚点同一条撤销记录
        var before = (ImageBlock)core.Document.Blocks[1];
        Assert.Equal(new FloatAnchor(0, 0), before.Float!.Anchor);
        Assert.Equal(120f, before.Width);
        Assert.Equal(80f, before.Height);
    }

    [Fact]
    public void ResizeImage_SameSizeSameAnchor_NoChange()
    {
        var placed = new ImageBlock("img", 120f, 80f,
            new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 2), null, AnchorToChar: true));
        var state = StateWith(new ParagraphBlock("文本"), placed);
        var after = new ResizeImageCommand(1, 120f, 80f, new FloatAnchor(0, 2)).Apply(state);
        Assert.Same(state.Document, after.Document);
    }

    // ---------------- 拖动预览的锚定语义（引擎侧） ----------------

    [Fact]
    public void AnchoredImage_ResolvesToAnchorLineTop_AfterMove()
    {
        // 三行文本（每行 20dip）+ 锚到第 2 行的图片：图片 Y = 第 2 行行盒顶缘
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            AnchoredImage(60f, 40f, FloatSide.Left, new FloatAnchor(1, 0)),
        };
        using var result = engine.Layout(new Document(blocks), 200f);

        var floatObject = Assert.Single(result.Floats);
        Assert.Equal(20f, floatObject.Rect.Y); // 第 2 块首行顶缘
        Assert.Equal(0f, floatObject.Rect.X);  // 左浮动贴左缘
    }

    [Fact]
    public void AnchoredImage_AnchorToChar_SitsAfterAnchorCharacter()
    {
        // 一行 10 字符（每字 10dip）：锚到第 5 个字符之后 → 图片 X = 50（不贴左右缘）
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ImageBlock("img", 40f, 20f,
                new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 5), null,
                    AnchorToChar: true)),
        };
        using var result = engine.Layout(new Document(blocks), 200f);

        var placed = Assert.Single(result.Floats);
        Assert.Equal(50f, placed.Rect.X);
        Assert.Equal(0f, placed.Rect.Y);
    }

    [Fact]
    public void AnchoredImage_AnchorToChar_AtLineEnd_SitsAtLineEnd()
    {
        // 短行（5 字符 = 50dip）末尾锚定：X = 行尾 50（不是下一行行首 0）
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(5)),
            new ImageBlock("img", 40f, 20f,
                new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 5), null,
                    AnchorToChar: true)),
        };
        using var result = engine.Layout(new Document(blocks), 200f);
        Assert.Equal(50f, Assert.Single(result.Floats).Rect.X);
    }

    [Fact]
    public void AnchoredImage_FreeX_MidLine_TextFlowsBothSides()
    {
        // 横向落到行中：同一视觉行应切成左右两段（文字绕在两侧）
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(12)),
            new ImageBlock("img", 40f, 20f,
                new FloatPlacement(FloatSide.Right, 0f, new FloatAnchor(0, 4), null,
                    AnchorToChar: true)),
        };
        using var result = engine.Layout(new Document(blocks), 120f);

        // 图片 [40, 80]，首行可用段 = [0,44] 与 [76,120]（排除区两侧各内缩 4dip）
        var rowLines = result.Lines.Where(l => l.BlockIndex == 0 && l.Y == 0f).ToList();
        Assert.Equal(2, rowLines.Count);
        Assert.Equal(0f, rowLines[0].X);
        Assert.Equal(76f, rowLines[1].X);
    }

    // ---------------- 拖动落点的锚定规则（HitTestFloatAnchor，Phase 3 M4 定稿）----------------

    private static LayoutResult NaturalLayout(params Block[] blocks) =>
        new FlowLayoutEngine(new FakeTextMeasurer()).Layout(new Document(blocks), 200f);

    [Fact]
    public void FloatAnchor_RightOfHalfLine_SnapsToLineEnd()
    {
        // 半行（5 字符 = 50dip）：图片落在行尾右侧 → 锚到行尾（最后一个字之后）
        using var layout = NaturalLayout(new ParagraphBlock(FakeTextMeasurer.Text(5)));
        var hit = layout.HitTestFloatAnchor(new LayoutRect(120f, 2f, 60f, 40f));
        Assert.True(hit.Found);
        Assert.Equal(0, hit.BlockIndex);
        Assert.Equal(5, hit.CharIndex);
    }

    [Fact]
    public void FloatAnchor_OverText_SnapsAfterCharAtLeftEdge()
    {
        // 图片左缘落在第 4 个字（第 3 号字符，单元格 30–40）右半：锚在它之后
        using var layout = NaturalLayout(new ParagraphBlock(FakeTextMeasurer.Text(10)));
        var hit = layout.HitTestFloatAnchor(new LayoutRect(35f, 5f, 40f, 20f));
        Assert.Equal(4, hit.CharIndex);
    }

    [Fact]
    public void FloatAnchor_SpansTwoLines_TakesFirstOccludedLine()
    {
        // 25 字符两行；图片纵向跨两行 → 取第一条覆盖到的行（首行）
        using var layout = NaturalLayout(new ParagraphBlock(FakeTextMeasurer.Text(25)));
        var hit = layout.HitTestFloatAnchor(new LayoutRect(0f, 15f, 40f, 60f));
        Assert.Equal(0, hit.CharIndex);
    }

    [Fact]
    public void FloatAnchor_BelowAllText_SnapsUpToLastLineEnd()
    {
        // 5 字符一行；图片在全部文字下方、横向超出文本 → 向上吸到末行行尾
        using var layout = NaturalLayout(new ParagraphBlock(FakeTextMeasurer.Text(5)));
        var hit = layout.HitTestFloatAnchor(new LayoutRect(120f, 100f, 60f, 40f));
        Assert.Equal(5, hit.CharIndex);
    }

    [Fact]
    public void FloatAnchor_OnEmptyLine_SnapsToLineStart()
    {
        // 空行（只有回车）：图片落在空行上 → 锚到该行行首
        using var layout = NaturalLayout(
            new ParagraphBlock("abc"),
            new ParagraphBlock(""));
        var hit = layout.HitTestFloatAnchor(new LayoutRect(10f, 22f, 40f, 20f));
        Assert.Equal(1, hit.BlockIndex);
        Assert.Equal(0, hit.CharIndex);
    }

    // ---------------- 锚点随编辑维护（Phase 3 M4）----------------

    private static ImageBlock ImageAnchoredTo(int blockIndex, int charIndex) =>
        new("img", 60f, 40f, new FloatPlacement(FloatSide.Right, 4f,
            new FloatAnchor(blockIndex, charIndex), null, AnchorToChar: true));

    [Fact]
    public void SplitBlock_BeforeAnchor_MovesAnchorToSecondHalf()
    {
        var state = StateWith(new ParagraphBlock("abcdef"), ImageAnchoredTo(0, 4));
        var after = new SplitBlockCommand(new TextPosition(0, 2)).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[2]);
        Assert.Equal(new FloatAnchor(1, 2), image.Float!.Anchor); // 4 - 2
    }

    [Fact]
    public void SplitBlock_AboveAnchor_ShiftsAnchorBlock()
    {
        var state = StateWith(
            new ParagraphBlock("ab"),
            new ParagraphBlock("cd"),
            ImageAnchoredTo(1, 1));
        var after = new SplitBlockCommand(new TextPosition(0, 1)).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[3]);
        Assert.Equal(new FloatAnchor(2, 1), image.Float!.Anchor);
    }

    [Fact]
    public void InsertText_BeforeAnchor_ShiftsAnchorCharIndex()
    {
        var state = StateWith(new ParagraphBlock("abcdef"), ImageAnchoredTo(0, 4))
            with { Selection = TextRange.Collapse(new TextPosition(0, 1)) };
        var after = new InsertTextCommand("XY").Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(new FloatAnchor(0, 6), image.Float!.Anchor); // 4 + 2
    }

    [Fact]
    public void DeleteRange_BeforeAnchor_ShiftsAnchorCharIndex()
    {
        var state = StateWith(new ParagraphBlock("abcdef"), ImageAnchoredTo(0, 4));
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(0, 3));
        var after = new DeleteRangeCommand(range).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(new FloatAnchor(0, 2), image.Float!.Anchor); // 4 - 2
    }

    [Fact]
    public void MergeBlock_AnchorInMergedBlock_MovesToPreviousBlock()
    {
        var state = StateWith(
            new ParagraphBlock("abc"),
            new ParagraphBlock("de"),
            ImageAnchoredTo(1, 1));
        var after = new MergeBlockCommand(1).Apply(state);

        var image = Assert.IsType<ImageBlock>(after.Document.Blocks[1]);
        Assert.Equal(new FloatAnchor(0, 4), image.Float!.Anchor); // 3（前块长）+ 1
    }

    [Fact]
    public void InsertImage_ShiftsLaterAnchors()
    {
        var state = StateWith(
            new ParagraphBlock("aa"),
            new ParagraphBlock("bb"),
            ImageAnchoredTo(1, 1))
            with { Selection = TextRange.Collapse(new TextPosition(0, 0)) };
        var after = new InsertImageCommand("img2", [1, 2, 3], "image/png", 40f, 30f).Apply(state);

        // 新图片插到块 0 之后（索引 1）：原文本块与图片块整体后移，锚点指向新块 2
        Assert.Equal(4, after.Document.Blocks.Count);
        var moved = Assert.IsType<ImageBlock>(after.Document.Blocks[3]);
        Assert.Equal(new FloatAnchor(2, 1), moved.Float!.Anchor);
    }

    [Fact]
    public void AnchorFollowsText_InsertLineAbove_ImageMovesDown()
    {
        var engine = new FlowLayoutEngine(new FakeTextMeasurer());
        var blocks = new Block[]
        {
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            new ParagraphBlock(FakeTextMeasurer.Text(10)),
            ImageAnchoredTo(1, 0), // 锚在第 2 块首字之后 → 图片 Y = 20
        };
        using (var before = engine.Layout(new Document(blocks), 200f))
        {
            Assert.Equal(20f, Assert.Single(before.Floats).Rect.Y);
        }

        // 在第 1 块行首分块（等同上方向上插入一行）后重新排版：图片跟着锚点下移一行
        var state = EditorState.Initial(new Document(blocks));
        var after = new SplitBlockCommand(new TextPosition(0, 0)).Apply(state);
        using var result = engine.Layout(after.Document, 200f);

        var placed = Assert.Single(result.Floats);
        Assert.Equal(40f, placed.Rect.Y);
    }
}
