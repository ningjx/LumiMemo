using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.Core.Tests;

/// <summary>
/// 等宽假字体度量器：每字符 10dip 宽，Ascent 16 / Descent 4（行高 20）。
/// 所有环绕用例因此获得确定性断言（无需 GPU、无字体差异）。
/// </summary>
internal sealed class FakeTextMeasurer : ITextMeasurer
{
    public const float CharWidth = 10f;
    public const float Ascent = 16f;
    public const float Descent = 4f;
    public const float LineHeight = Ascent + Descent;

    public FirstLineInfo LayoutFirstLine(string text, TextStyle style, float maxWidth)
    {
        int fit = (int)(maxWidth / CharWidth);
        int consumed = Math.Min(text.Length, fit);
        return consumed <= 0
            ? default
            : new FirstLineInfo(consumed, consumed * CharWidth, Ascent, Descent, null);
    }

    public LineHeightInfo MeasureLineHeight(TextStyle style) => new(Ascent, Descent);

    /// <summary>造一段指定长度的文本（内容无意义，仅字符数驱动断言）。</summary>
    public static string Text(int chars)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var buffer = new char[chars];
        for (int i = 0; i < chars; i++)
        {
            buffer[i] = alphabet[i % alphabet.Length];
        }
        return new string(buffer);
    }
}
