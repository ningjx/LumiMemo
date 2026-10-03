using LumiText.Core.Documents;
using LumiText.Core.Editing;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// RtfProjection 单测（Phase 2 设计 §7/§10.1）：最小集往返（粗斜删下划 + 颜色 + 段落）。
/// </summary>
public sealed class RtfProjectionTests
{
    [Fact]
    public void RoundTrip_BoldItalicUnderlineStrike_Preserved()
    {
        var doc = new Document([
            new ParagraphBlock([
                new TextRun("普通"),
                new TextRun("加粗", new InlineStyle(Bold: true)),
                new TextRun("斜体", new InlineStyle(Italic: true)),
                new TextRun("下划", new InlineStyle(Underline: true)),
                new TextRun("删线", new InlineStyle(Strikethrough: true)),
            ]),
        ]);

        string rtf = RtfProjection.ToRtf(doc);
        var back = RtfProjection.FromRtf(rtf);

        var p = Assert.IsType<ParagraphBlock>(back.Blocks[0]);
        Assert.Equal("普通加粗斜体下划删线", p.PlainText);
        Assert.True(p.Runs[1].Style!.Bold);
        Assert.True(p.Runs[2].Style!.Italic);
        Assert.True(p.Runs[3].Style!.Underline);
        Assert.True(p.Runs[4].Style!.Strikethrough);
    }

    [Fact]
    public void RoundTrip_Color_Preserved()
    {
        var red = new Color32(255, 200, 30, 40);
        var doc = new Document([
            new ParagraphBlock([new TextRun("彩色", new InlineStyle(Color: red))]),
        ]);

        string rtf = RtfProjection.ToRtf(doc);
        Assert.Contains(@"\colortbl", rtf);
        Assert.Contains(@"\red200\green30\blue40", rtf);

        var back = RtfProjection.FromRtf(rtf);
        var p = Assert.IsType<ParagraphBlock>(back.Blocks[0]);
        Assert.Equal(red, p.Runs[0].Style!.Color);
    }

    [Fact]
    public void RoundTrip_MultipleParagraphs_Preserved()
    {
        var doc = new Document([
            new ParagraphBlock("第一段"),
            new ParagraphBlock("第二段"),
            new ParagraphBlock("第三段"),
        ]);

        var back = RtfProjection.FromRtf(RtfProjection.ToRtf(doc));
        Assert.Equal(3, back.Blocks.Count);
        Assert.Equal("第一段", ((ParagraphBlock)back.Blocks[0]).PlainText);
        Assert.Equal("第二段", ((ParagraphBlock)back.Blocks[1]).PlainText);
        Assert.Equal("第三段", ((ParagraphBlock)back.Blocks[2]).PlainText);
    }

    [Fact]
    public void ToRtf_Heading_CarriesFontSizeAndBlockBold()
    {
        // 标题的加粗在块级 EffectiveStyle 上（run 无显式样式）：复制到 Word 也要粗 + 大字号
        var doc = new Document([new HeadingBlock([new TextRun("标题")], level: 1)]);

        string rtf = RtfProjection.ToRtf(doc);
        Assert.Contains(@"\b", rtf);
        Assert.Contains(@"\fs44", rtf); // H1 22pt → RTF 半磅 44
    }

    [Fact]
    public void RoundTrip_Unicode_EscapedAndRestored()
    {
        var doc = new Document([new ParagraphBlock("中文、Emoji🎉、日文テスト")]);
        var back = RtfProjection.FromRtf(RtfProjection.ToRtf(doc));
        Assert.Equal("中文、Emoji🎉、日文テスト", ((ParagraphBlock)back.Blocks[0]).PlainText);
    }

    [Fact]
    public void RoundTrip_RtfReservedChars_Escaped()
    {
        var doc = new Document([new ParagraphBlock(@"含{大括号}和\反斜杠")]);
        var back = RtfProjection.FromRtf(RtfProjection.ToRtf(doc));
        Assert.Equal(@"含{大括号}和\反斜杠", ((ParagraphBlock)back.Blocks[0]).PlainText);
    }

    [Fact]
    public void FromRtf_ExternalWordRtf_ReadsStylesAndText()
    {
        // 模拟 Word 导出的 RTF（含 fonttbl/colortbl 组与样式控制词）。
        // RTF 是流式格式：\b 无 \b0 关闭则样式持续——「加粗和」连读、「红色文字」继承 bold。
        const string wordRtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 Calibri;}}{\colortbl ;\red255\green0\blue0;}\pard\plain\f0\fs22 这是{\b 加粗}和{\cf1 红色}文字\par 第二行}";
        var doc = RtfProjection.FromRtf(wordRtf);
        Assert.Equal(2, doc.Blocks.Count);
        var first = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Equal("这是加粗和红色文字", first.PlainText);
        // fonttbl/colortbl 组内容不泄漏进正文
        Assert.DoesNotContain("Calibri", first.PlainText);
        // 加粗段（\b 起、未关闭则延续）
        Assert.Contains(first.Runs, r => r.Text.Contains("加粗") && r.Style?.Bold == true);
        // 红色段（colortbl 第 1 条 = 红 255,0,0）
        Assert.Contains(first.Runs, r => r.Text.Contains("红色") && r.Style?.Color?.R == 255);
        var second = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Equal("第二行", second.PlainText);
    }

    [Fact]
    public void FromRtf_PlainTextFallback_NoStyles()
    {
        const string plain = @"{\rtf1\ansi 纯文本内容}";
        var doc = RtfProjection.FromRtf(plain);
        var p = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Contains("纯文本内容", p.PlainText);
    }

    [Fact]
    public void ToRtf_Heading_UsesAbsoluteFontSize()
    {
        var doc = new Document([new HeadingBlock("标题", 1)]);
        string rtf = RtfProjection.ToRtf(doc);
        Assert.Contains(@"\fs44", rtf); // H1 = 22dip × 2 = 44 半磅
    }
}
