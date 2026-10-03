using LumiText.Core.Documents;
using LumiText.Core.Documents.Serialization;
using Xunit;

namespace LumiText.Core.Tests;

/// <summary>
/// T-S 系列：.lumi v2 正文 schema 契约（Phase 1 设计 §3.3/§10.1）。
/// 等价性以「往返后再次序列化的 JSON 字符串相等」钉死（record 集合属性是引用相等，
/// 不能直接 Assert.Equal 两个 Document），辅以结构抽查。
/// </summary>
public sealed class DocumentSerializationTests
{
    // T-S1：全块型 + 行内样式 + 浮动锚定 + 图片表的往返相等
    [Fact]
    public void RoundTrip_AllBlockTypes_PreservesEverything()
    {
        var doc = new Document(
            [
                new ParagraphBlock(
                    [new TextRun("普通文字"), new TextRun("加粗", new InlineStyle(Bold: true))],
                    spaceAfter: 6f),
                new HeadingBlock("标题", 1),
                new TodoBlock([new TextRun("待办事项")], @checked: true),
                new DividerBlock(),
                new ImageBlock("img-1", 240, 160,
                    new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(0, 0))),
                new ImageBlock("img-2", 100, 80,
                    new FloatPlacement(FloatSide.Left, 4f, Position: new FloatPosition(24, 48))),
                new ParagraphBlock(
                    [new TextRun("彩色", new InlineStyle(
                        Italic: true, Strikethrough: true, Underline: true,
                        Color: new Color32(255, 180, 40, 60), FontSizeRatio: 1.5f))],
                    style: new TextStyle("Cascadia Mono", 13f)),
            ],
            [new ImageResource("img-1", "image/png", [1, 2, 3, 4])]);

        string json1 = DocumentSerializer.Serialize(doc);
        // 契约抽查：中文原文写入（UnsafeRelaxedJsonEscaping，与宿主仓约定对齐）；
        // 锚点三字段恒写出（0 是主流值，不能被默认值省略吞掉）。
        Assert.Contains("普通文字", json1);
        Assert.Contains("\"block\": 0", json1);

        var doc2 = DocumentSerializer.Deserialize(json1);
        Assert.NotNull(doc2);
        string json2 = DocumentSerializer.Serialize(doc2);
        Assert.Equal(json1, json2);

        // 结构抽查：块型、判别值、样式、锚点。
        Assert.Equal(7, doc2.Blocks.Count);
        var paragraph = Assert.IsType<ParagraphBlock>(doc2.Blocks[0]);
        Assert.Equal(2, paragraph.Runs.Count);
        Assert.Equal("普通文字加粗", paragraph.PlainText);
        Assert.True(paragraph.Runs[1].Style!.Bold);
        Assert.Equal(6f, paragraph.SpaceAfter);

        var heading = Assert.IsType<HeadingBlock>(doc2.Blocks[1]);
        Assert.Equal(1, heading.Level);
        Assert.Equal(22f, heading.EffectiveStyle.FontSize);

        var todo = Assert.IsType<TodoBlock>(doc2.Blocks[2]);
        Assert.True(todo.Checked);

        Assert.IsType<DividerBlock>(doc2.Blocks[3]);

        var anchored = Assert.IsType<ImageBlock>(doc2.Blocks[4]);
        Assert.Equal(FloatSide.Right, anchored.Float!.Side);
        Assert.Equal(0, anchored.Float.Anchor!.BlockIndex);

        var positioned = Assert.IsType<ImageBlock>(doc2.Blocks[5]);
        Assert.Null(positioned.Float!.Anchor);
        Assert.Equal(24f, positioned.Float.Position!.Value.X);

        var styled = Assert.IsType<ParagraphBlock>(doc2.Blocks[6]);
        var inline = styled.Runs[0].Style!;
        Assert.Equal(new Color32(255, 180, 40, 60), inline.Color);
        Assert.Equal(1.5f, inline.FontSizeRatio);
        Assert.Equal("Cascadia Mono", styled.Style!.FontFamily);
    }

    // T-S2：缺省字段降级（最小 JSON → 默认值）
    [Fact]
    public void Deserialize_MissingFields_FallsBackToDefaults()
    {
        const string json = """
            {"schema":1,"blocks":[
              {"type":"paragraph"},
              {"type":"todo","runs":[{"t":"x"}]},
              {"type":"divider"},
              {"type":"image","imageId":"i","width":10,"height":10}
            ]}
            """;

        var doc = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc);
        Assert.Equal(4, doc.Blocks.Count);

        var paragraph = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Empty(paragraph.Runs);
        Assert.Null(paragraph.Style);
        Assert.Equal(0f, paragraph.SpaceAfter);
        Assert.Equal(TextStyle.Default, paragraph.EffectiveStyle);

        var todo = Assert.IsType<TodoBlock>(doc.Blocks[1]);
        Assert.False(todo.Checked);

        var image = Assert.IsType<ImageBlock>(doc.Blocks[3]);
        Assert.Null(image.Float);
        Assert.Null(doc.Images);
    }

    // T-S3：未知字段忽略（根/块/run/样式各层级都塞未来字段）
    [Fact]
    public void Deserialize_UnknownFields_AreIgnored()
    {
        const string json = """
            {"schema":1,"futureRoot":{"x":1},"blocks":[
              {"type":"paragraph","futureBlock":42,"runs":[
                {"t":"a","futureRun":true,"s":{"b":true,"futureStyle":"zzz"}}
              ]}
            ],"futureTail":[1,2,3]}
            """;

        var doc = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc);
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("a", paragraph.PlainText);
        Assert.True(paragraph.Runs[0].Style!.Bold);
    }

    // T-S4：schema 高于当前版本 → 跳过（返回 null）
    [Fact]
    public void Deserialize_NewerSchema_ReturnsNull()
    {
        Assert.Null(DocumentSerializer.Deserialize("""{"schema":99,"blocks":[]}"""));
        Assert.Null(DocumentSerializer.Deserialize("""{"schema":2}"""));
    }

    // T-S5：base64 图片往返字节一致
    [Fact]
    public void RoundTrip_ImageData_ByteIdentical()
    {
        var data = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }
        var doc = new Document(
            [new ImageBlock("img-1", 1, 1)],
            [new ImageResource("img-1", "image/png", data)]);

        var doc2 = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.NotNull(doc2);
        var resource = Assert.Single(doc2.Images!);
        Assert.Equal("img-1", resource.Id);
        Assert.Equal("image/png", resource.Mime);
        Assert.Equal(data, resource.Data);
    }

    // T-S6：Document.GetFloats 派生（锚定占位 / 直给位置 / 非浮动图片不派生）
    [Fact]
    public void GetFloats_DerivesFromImageBlocks()
    {
        var doc = new Document(
            [
                new ParagraphBlock("文本"),
                new ImageBlock("a", 240, 160,
                    new FloatPlacement(FloatSide.Right, 8f, new FloatAnchor(0, 4))),
                new ImageBlock("b", 100, 80,
                    new FloatPlacement(FloatSide.Left, 4f, Position: new FloatPosition(24, 48))),
                new ImageBlock("c", 50, 50),
            ]);

        var floats = doc.GetFloats();
        Assert.Equal(2, floats.Count);

        Assert.Equal(1, floats[0].Id);   // 块索引
        Assert.Equal(new FloatAnchor(0, 4), floats[0].Anchor);
        Assert.Equal(240f, floats[0].Rect.Width);   // 锚定占位：原点 + 显示尺寸
        Assert.Equal(8f, floats[0].Margin);

        Assert.Equal(2, floats[1].Id);
        Assert.Null(floats[1].Anchor);
        Assert.Equal(24f, floats[1].Rect.X);
        Assert.Equal(80f, floats[1].Rect.Height);
    }

    // T-S8：bullet 段落往返（isBullet 写出/读回）；老 JSON 无该字段按缺省 false 降级
    [Fact]
    public void RoundTrip_BulletParagraph_PreservesFlag()
    {
        var doc = new Document(
        [
            new ParagraphBlock("分点项", isBullet: true),
            new ParagraphBlock("普通段"),
        ]);

        string json = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"isBullet\": true", json);

        var doc2 = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc2);
        Assert.True(Assert.IsType<ParagraphBlock>(doc2.Blocks[0]).IsBullet);
        Assert.False(Assert.IsType<ParagraphBlock>(doc2.Blocks[1]).IsBullet);
        Assert.Equal(json, DocumentSerializer.Serialize(doc2));

        // 老 JSON（无 isBullet 字段）→ 缺省 false（DefaultIgnoreCondition.WhenWritingDefault
        // 也意味着非 bullet 段落写出时不带该字段）
        var legacy = DocumentSerializer.Deserialize(
            """{"schema":1,"blocks":[{"type":"paragraph","runs":[{"t":"x"}]}]}""");
        Assert.NotNull(legacy);
        Assert.False(Assert.IsType<ParagraphBlock>(legacy.Blocks[0]).IsBullet);
    }

    // T-S7：schema 1 旧锚点格式（block/x/y）降级读取——x/y 被未知字段忽略吞掉，
    // char 缺省降级为 0，等价于「块首字符」；schema 高于当前版本 → null（高版本跳过）
    [Fact]
    public void Deserialize_LegacyAnchorFormat_DegradesToCharZero()
    {
        const string json = """
            {"schema":1,"blocks":[
              {"type":"paragraph","runs":[{"t":"文本"}]},
              {"type":"image","imageId":"a","width":240,"height":160,
               "float":{"side":"right","margin":8,"anchor":{"block":0,"x":4.0,"y":12.0}}}
            ]}
            """;

        var doc = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc);

        var image = Assert.IsType<ImageBlock>(doc.Blocks[1]);
        Assert.Equal(new FloatAnchor(0, 0), image.Float!.Anchor);

        // 高版本 schema → null
        Assert.Null(DocumentSerializer.Deserialize("""{"schema":99,"blocks":[]}"""));
    }

    // T-S9（Phase 3 打磨改版）：文字底色（行内背景）往返——run 样式的 bg 写出/读回；
    // 无底色 run 不写出该字段；老 JSON 无该字段读为 null
    [Fact]
    public void RoundTrip_InlineBackground_PreservesColor()
    {
        var highlight = new Color32(0x66, 0xFF, 0xD9, 0x66);
        var doc = new Document(
        [
            new ParagraphBlock([
                new TextRun("带底色", new InlineStyle(Background: highlight)),
                new TextRun("普通"),
            ]),
        ]);

        string json = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"bg\": \"#66FFD966\"", json);
        Assert.Equal(1, json.Split("\"bg\"").Length - 1); // 只有带底色的 run 写出该字段

        var doc2 = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc2);
        var paragraph = Assert.IsType<ParagraphBlock>(doc2.Blocks[0]);
        Assert.Equal(highlight, paragraph.Runs[0].Style!.Background);
        Assert.Null(paragraph.Runs[1].Style?.Background);
        Assert.Equal(json, DocumentSerializer.Serialize(doc2));

        var legacy = DocumentSerializer.Deserialize(
            """{"schema":1,"blocks":[{"type":"paragraph","runs":[{"t":"x","s":{"b":true}}]}]}""");
        Assert.NotNull(legacy);
        Assert.Null(Assert.IsType<ParagraphBlock>(legacy.Blocks[0]).Runs[0].Style!.Background);
    }

    // T-S10：段落级粗体（Phase 3 M2）——ParagraphBlock.Style 带 bold 往返；bold=false 不写出；
    // 老 JSON 无该字段读为 false
    [Fact]
    public void RoundTrip_TextStyleBold_PreservesFlag()
    {
        var doc = new Document(
        [
            new ParagraphBlock("粗体段", style: new TextStyle("Segoe UI", 14f, Bold: true)),
            new ParagraphBlock("普通段", style: new TextStyle("Segoe UI", 14f)),
        ]);

        string json = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"bold\": true", json);
        Assert.Equal(1, json.Split("\"bold\"").Length - 1); // false 不写出

        var doc2 = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc2);
        Assert.True(Assert.IsType<ParagraphBlock>(doc2.Blocks[0]).Style!.Bold);
        Assert.False(Assert.IsType<ParagraphBlock>(doc2.Blocks[1]).Style!.Bold);
        Assert.Equal(json, DocumentSerializer.Serialize(doc2));

        var legacy = DocumentSerializer.Deserialize(
            """{"schema":1,"blocks":[{"type":"paragraph","runs":[{"t":"x"}],"style":{"font":"Segoe UI","size":14}}]}""");
        Assert.False(Assert.IsType<ParagraphBlock>(legacy!.Blocks[0]).Style!.Bold);
    }

    // T-S11：浮动图片"紧跟锚字符"标记（Phase 3 M4）——anchorChar 写出/读回；缺省（false）不写出
    [Fact]
    public void RoundTrip_FloatAnchorToChar_PreservesFlag()
    {
        var doc = new Document(
        [
            new ParagraphBlock("文本"),
            new ImageBlock("img", 120f, 80f,
                new FloatPlacement(FloatSide.Right, 4f, new FloatAnchor(0, 0), null,
                    AnchorToChar: true)),
            new ImageBlock("img2", 120f, 80f,
                new FloatPlacement(FloatSide.Right, 4f, new FloatAnchor(0, 0))),
        ]);

        string json = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"anchorChar\": true", json);
        Assert.Equal(1, json.Split("\"anchorChar\"").Length - 1); // 未启用不写出

        var doc2 = DocumentSerializer.Deserialize(json);
        Assert.NotNull(doc2);
        Assert.True(Assert.IsType<ImageBlock>(doc2.Blocks[1]).Float!.AnchorToChar);
        Assert.False(Assert.IsType<ImageBlock>(doc2.Blocks[2]).Float!.AnchorToChar);
        Assert.Equal(json, DocumentSerializer.Serialize(doc2));
    }
}
