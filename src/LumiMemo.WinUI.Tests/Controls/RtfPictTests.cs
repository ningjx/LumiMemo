using LumiMemo.WinUI.Controls;
using Xunit;

namespace LumiMemo.WinUI.Tests.Controls;

/// <summary>
/// <see cref="RtfPict"/> 的单元测试：RTF 片段里 <c>\pict</c> 的尺寸读写与像素提取。
/// </summary>
/// <remarks>
/// 片段样本按 RichEdit 的输出约定合成：blip 词 → 尺寸控制词 → 空格 → 十六进制像素 → <c>}</c>。
/// 真机上如果形态和预期有出入，先在这里补一个真实样本把断言钉住，再改解析。
/// </remarks>
public sealed class RtfPictTests
{
    private const string Hex = "89504e470d0a1a0a0000000d49484452";

    private static string PngWithGoals(int goalW, int goalH) =>
        $@"{{\pict\pngblip\picw250\pich100\picwgoal{goalW}\pichgoal{goalH} {Hex}}}";

    // ---- 读显示尺寸 ----

    [Fact]
    public void 有goal_读出显示尺寸()
    {
        Assert.True(RtfPict.TryGetDisplaySize(PngWithGoals(3750, 1500), out double w, out double h));
        Assert.Equal(250, w, 3);
        Assert.Equal(100, h, 3);
    }

    [Fact]
    public void 无goal_用原始尺寸兜底()
    {
        Assert.True(RtfPict.TryGetDisplaySize(@"{\pict\pngblip\picw240\pich120 00112233}", out double w, out double h));
        Assert.Equal(240, w, 3);
        Assert.Equal(120, h, 3);
    }

    [Fact]
    public void 只有picscale_乘百分比换算()
    {
        Assert.True(RtfPict.TryGetDisplaySize(
            @"{\pict\pngblip\picw100\pich50\picscalex200\picscaley150 00}", out double w, out double h));
        Assert.Equal(200, w, 3);
        Assert.Equal(75, h, 3);
    }

    [Fact]
    public void 非图片片段_返回失败()
    {
        Assert.False(RtfPict.TryGetDisplaySize(@"{\rtf1 你好}", out _, out _));
        Assert.False(RtfPict.TryGetDisplaySize(string.Empty, out _, out _));
    }

    // ---- 改写尺寸 ----

    [Fact]
    public void 改写goal_比例正确_其余内容逐字节保留()
    {
        Assert.True(RtfPict.TryResize(PngWithGoals(3750, 1500), 500, 200, out string resized));

        Assert.Contains(@"\picwgoal7500", resized);
        Assert.Contains(@"\pichgoal3000", resized);
        Assert.Contains(Hex, resized);
        Assert.Contains(@"\picw250\pich100", resized);
        Assert.DoesNotContain(@"\picwgoal3750", resized);
    }

    [Fact]
    public void 只改宽_高保持()
    {
        Assert.True(RtfPict.TryResize(PngWithGoals(3750, 1500), 125, 100, out string resized));

        Assert.Contains(@"\picwgoal1875", resized);
        Assert.Contains(@"\pichgoal1500", resized);
    }

    [Fact]
    public void 无goal_在blip词后插入goal并留空格终止()
    {
        Assert.True(RtfPict.TryResize(@"{\pict\pngblip\picw240\pich120 00112233}", 480, 240, out string resized));

        Assert.Contains(@"\pngblip\picwgoal7200\pichgoal3600 \picw240", resized);
    }

    [Fact]
    public void 只有picscale_改百分比()
    {
        Assert.True(RtfPict.TryResize(
            @"{\pict\pngblip\picw100\pich50\picscalex200\picscaley200 00}", 100, 50, out string resized));

        Assert.Contains(@"\picscalex100", resized);
        Assert.Contains(@"\picscaley100", resized);
    }

    [Fact]
    public void 目标尺寸非法_拒绝()
    {
        Assert.False(RtfPict.TryResize(PngWithGoals(3750, 1500), 0, 100, out _));
        Assert.False(RtfPict.TryResize(PngWithGoals(3750, 1500), 100, -1, out _));
    }

    [Fact]
    public void 无图片_改写失败且片段原样返回()
    {
        Assert.False(RtfPict.TryResize(@"{\rtf1 你好}", 100, 100, out string resized));
        Assert.Equal(@"{\rtf1 你好}", resized);
    }

    // ---- 提取像素 ----

    [Fact]
    public void 提取位图_字节与类型正确()
    {
        Assert.True(RtfPict.TryExtractImage(PngWithGoals(3750, 1500), out byte[] data, out string kind));

        Assert.Equal("pngblip", kind);
        Assert.Equal(16, data.Length);
        Assert.Equal(0x89, data[0]);
        Assert.Equal(0x52, data[^1]);
        Assert.True(RtfPict.IsRaster(kind));
    }

    [Fact]
    public void 提取位图_十六进制带换行_结果一致()
    {
        string fragment = "{\\pict\\pngblip\\picw2\\pich2 " + Hex[..8] + "\r\n" + Hex[8..] + "}";

        Assert.True(RtfPict.TryExtractImage(fragment, out byte[] data, out _));
        Assert.Equal(16, data.Length);
        Assert.Equal(0x89, data[0]);
    }

    [Fact]
    public void 提取矢量图_类型识别_标记为非位图()
    {
        Assert.True(RtfPict.TryExtractImage(@"{\pict\wmetafile8\picw10\pich10 01020304}", out byte[] data, out string kind));

        Assert.Equal("wmetafile8", kind);
        Assert.False(RtfPict.IsRaster(kind));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data);
    }

    [Fact]
    public void 十六进制长度为奇数_提取失败()
    {
        Assert.False(RtfPict.TryExtractImage(@"{\pict\pngblip 0a0}", out _, out _));
    }

    [Fact]
    public void 无图片_提取失败()
    {
        Assert.False(RtfPict.TryExtractImage(@"{\rtf1 你好}", out _, out _));
        Assert.False(RtfPict.TryExtractImage(string.Empty, out _, out _));
    }

    [Fact]
    public void 含图片的片段_ContainsPict为真()
    {
        Assert.True(RtfPict.ContainsPict(PngWithGoals(3750, 1500)));
        Assert.False(RtfPict.ContainsPict(@"{\rtf1 你好}"));
    }
}
