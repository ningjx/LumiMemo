using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LumiMemo.WinUI.Controls;

/// <summary>RTF 片段里 <c>\pict</c> 图片的读写：读显示尺寸、按比例改写尺寸、提取像素数据。</summary>
/// <remarks>
/// <para>
/// 用到的 RTF 约定：<c>\picw/\pich</c> = 原始尺寸，<c>\picwgoal/\pichgoal</c> = 显示尺寸
/// （twips，96dpi 下 ÷15 = px），<c>\picscalex/\picscaley</c> = 百分比缩放；
/// blip 类型有 <c>\pngblip/\jpegblip/\wmetafile8</c> 等。
/// </para>
/// <para>
/// 全部按「解析不了就返回 false」容错：片段的实际形态由 RichEdit 输出决定，
/// 万一真机上和预期不同，只影响走降级路径，不会把正文弄坏。
/// 改写尺寸用<strong>比例</strong>完成（新值 = 旧值 × 目标 ÷ 实测），天然绕开 twips/DPI 的换算口径。
/// </para>
/// </remarks>
internal static partial class RtfPict
{
    private const double TwipsPerPixel = 15.0;

    /// <summary>片段里是否含图片。</summary>
    public static bool ContainsPict(string rtf) => rtf.Contains("\\pict", StringComparison.Ordinal);

    /// <summary>读图片的显示尺寸（px）。goal 缺失时按原始尺寸 × picscale 兜底估算。</summary>
    public static bool TryGetDisplaySize(string rtf, out double widthPx, out double heightPx)
    {
        widthPx = 0;
        heightPx = 0;

        if (!ContainsPict(rtf))
        {
            return false;
        }

        if (TryGetInt(GoalWidth(), rtf, out int goalW) && goalW > 0
            && TryGetInt(GoalHeight(), rtf, out int goalH) && goalH > 0)
        {
            widthPx = goalW / TwipsPerPixel;
            heightPx = goalH / TwipsPerPixel;
            return true;
        }

        if (TryGetInt(PicWidth(), rtf, out int width) && width > 0
            && TryGetInt(PicHeight(), rtf, out int height) && height > 0)
        {
            double scaleX = TryGetInt(ScaleX(), rtf, out int sx) && sx > 0 ? sx / 100.0 : 1.0;
            double scaleY = TryGetInt(ScaleY(), rtf, out int sy) && sy > 0 ? sy / 100.0 : 1.0;
            widthPx = width * scaleX;
            heightPx = height * scaleY;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 按目标显示尺寸（px）改写片段：有 goal 改 goal，只有 picscale 改 picscale，
    /// 都没有就在 blip 词后插入 goal。除目标数值外，其余内容（尤其像素十六进制）逐字节保留。
    /// </summary>
    public static bool TryResize(string rtf, double newWidthPx, double newHeightPx, out string resized)
    {
        resized = rtf;

        if (newWidthPx <= 0 || newHeightPx <= 0
            || !TryGetDisplaySize(rtf, out double widthPx, out double heightPx)
            || widthPx <= 0 || heightPx <= 0)
        {
            return false;
        }

        double ratioX = newWidthPx / widthPx;
        double ratioY = newHeightPx / heightPx;

        if (TryGetInt(GoalWidth(), rtf, out int goalW) && goalW > 0
            && TryGetInt(GoalHeight(), rtf, out int goalH) && goalH > 0)
        {
            resized = ReplaceFirst(resized, GoalWidth(), $"\\picwgoal{Scaled(goalW, ratioX)}");
            resized = ReplaceFirst(resized, GoalHeight(), $"\\pichgoal{Scaled(goalH, ratioY)}");
            return true;
        }

        if (TryGetInt(ScaleX(), rtf, out int scaleX) && scaleX > 0
            && TryGetInt(ScaleY(), rtf, out int scaleY) && scaleY > 0)
        {
            resized = ReplaceFirst(resized, ScaleX(), $"\\picscalex{Scaled(scaleX, ratioX)}");
            resized = ReplaceFirst(resized, ScaleY(), $"\\picscaley{Scaled(scaleY, ratioY)}");
            return true;
        }

        // 插入位置：blip 词之后（放 \pict 之后是兜底）。词组要用空格终止——
        // 不然紧跟其后的十六进制会被当成控制词的数字参数吞掉。
        int insertAt = BlipWord().Match(rtf) is { Success: true } blip
            ? blip.Index + blip.Length
            : rtf.IndexOf("\\pict", StringComparison.Ordinal) + "\\pict".Length;
        if (insertAt <= 0)
        {
            return false;
        }

        string inserted = $"\\picwgoal{Scaled((int)Math.Round(newWidthPx * TwipsPerPixel), 1)}"
            + $"\\pichgoal{Scaled((int)Math.Round(newHeightPx * TwipsPerPixel), 1)} ";
        resized = string.Concat(rtf.AsSpan(0, insertAt), inserted, rtf.AsSpan(insertAt));
        return true;
    }

    /// <summary>提取图片像素数据与 blip 类型（十六进制形态；二进制 <c>\bin</c> 不支持）。</summary>
    public static bool TryExtractImage(string rtf, out byte[] data, out string blipKind)
    {
        data = [];
        blipKind = string.Empty;

        Match blip = BlipWord().Match(rtf);
        if (!blip.Success)
        {
            return false;
        }

        blipKind = blip.Groups[1].Value;

        // 跳过 blip 之后的控制词（\picw250、\picwgoal3750…），停在第一个十六进制位。
        int i = blip.Index + blip.Length;
        while (i < rtf.Length)
        {
            char current = rtf[i];
            if (current == '\\')
            {
                int wordStart = i + 1;
                int wordEnd = wordStart;
                while (wordEnd < rtf.Length && char.IsLetter(rtf[wordEnd]))
                {
                    wordEnd++;
                }

                if (rtf[wordStart..wordEnd] == "bin")
                {
                    return false;
                }

                i = wordEnd;
                while (i < rtf.Length && (char.IsAsciiDigit(rtf[i]) || rtf[i] == '-'))
                {
                    i++;
                }
            }
            else if (char.IsWhiteSpace(current))
            {
                i++;
            }
            else
            {
                break;
            }
        }

        var hex = new StringBuilder();
        for (; i < rtf.Length; i++)
        {
            char current = rtf[i];
            if (current == '}')
            {
                break;
            }

            if (char.IsAsciiHexDigit(current))
            {
                hex.Append(current);
            }
            else if (!char.IsWhiteSpace(current))
            {
                break;
            }
        }

        if (hex.Length < 2 || hex.Length % 2 != 0)
        {
            return false;
        }

        var bytes = new byte[hex.Length / 2];
        for (int k = 0; k < bytes.Length; k++)
        {
            if (!byte.TryParse(hex.ToString(k * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[k]))
            {
                return false;
            }
        }

        data = bytes;
        return true;
    }

    /// <summary>blip 是否为位图（位图可以走「取字节重插」的降级路径；矢量图不行）。</summary>
    public static bool IsRaster(string blipKind) => blipKind is
        "pngblip" or "jpegblip" or "dibitmap0" or "dibitmap" or "wbitmap0" or "wbitmap";

    private static int Scaled(int value, double ratio) =>
        Math.Max(1, (int)Math.Round(value * ratio));

    private static bool TryGetInt(Regex regex, string input, out int value)
    {
        value = 0;
        Match match = regex.Match(input);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static string ReplaceFirst(string input, Regex regex, string replacement)
    {
        Match match = regex.Match(input);
        return match.Success
            ? string.Concat(input.AsSpan(0, match.Index), replacement, input.AsSpan(match.Index + match.Length))
            : input;
    }

    // \picw 与 \pich 是 \picwgoal/\pichgoal 的前缀，但控制词后面必须直接跟数字才算命中，
    // 所以 \picwgoal3750 不会被 \picw(\d+) 抢走。十六进制像素里没有反斜杠，匹配不会落到数据段。
    [GeneratedRegex(@"\\picwgoal(-?\d+)", RegexOptions.None)]
    private static partial Regex GoalWidth();

    [GeneratedRegex(@"\\pichgoal(-?\d+)", RegexOptions.None)]
    private static partial Regex GoalHeight();

    [GeneratedRegex(@"\\picw(-?\d+)", RegexOptions.None)]
    private static partial Regex PicWidth();

    [GeneratedRegex(@"\\pich(-?\d+)", RegexOptions.None)]
    private static partial Regex PicHeight();

    [GeneratedRegex(@"\\picscalex(-?\d+)", RegexOptions.None)]
    private static partial Regex ScaleX();

    [GeneratedRegex(@"\\picscaley(-?\d+)", RegexOptions.None)]
    private static partial Regex ScaleY();

    [GeneratedRegex(@"\\(pngblip|jpegblip|emfblip|wmetafile\d*|dibitmap\d*|wbitmap\d*)", RegexOptions.None)]
    private static partial Regex BlipWord();
}
