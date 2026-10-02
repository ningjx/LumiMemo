using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.Core.Tests;

/// <summary>
/// 等宽假字体度量器（M3 批量接口版）：字符宽 10dip × FontSizeRatio，
/// 行高 = (Ascent 16 + Descent 4) × 行内最大 ratio。所有环绕用例因此获得
/// 确定性断言（无需 GPU、无字体差异）；ratio 支持服务于 T-B3「行内大字撑高行高」。
/// </summary>
internal sealed class FakeTextMeasurer : ITextMeasurer
{
    public const float CharWidth = 10f;
    public const float Ascent = 16f;
    public const float Descent = 4f;
    public const float LineHeight = Ascent + Descent;

    public ILineBatch LayoutLines(IReadOnlyList<TextRun> runs, TextStyle baseStyle, float maxWidth)
    {
        int length = 0;
        foreach (var run in runs)
        {
            length += run.Text.Length;
        }
        var ratios = new float[length];
        int i = 0;
        foreach (var run in runs)
        {
            float ratio = run.Style?.FontSizeRatio ?? 1f;
            for (int j = 0; j < run.Text.Length; j++)
            {
                ratios[i++] = ratio;
            }
        }
        return new FakeLineBatch(ratios, maxWidth);
    }

    public LineHeightInfo MeasureLineHeight(TextStyle style) => new(Ascent, Descent);

    /// <summary>贪心填充：逐字符累计字宽不超过段宽；一个字符都放不下时 LineCount = 0（窄段放弃信号）。</summary>
    private sealed class FakeLineBatch : ILineBatch
    {
        private readonly List<MeasuredLine> _lines = new();

        public FakeLineBatch(float[] ratios, float maxWidth)
        {
            int pos = 0;
            float offsetY = 0f;
            while (pos < ratios.Length)
            {
                float width = 0f;
                int consumed = 0;
                float maxRatio = 0f;
                while (pos + consumed < ratios.Length)
                {
                    float ratio = ratios[pos + consumed];
                    float charWidth = CharWidth * ratio;
                    if (width + charWidth > maxWidth)
                    {
                        break;
                    }
                    width += charWidth;
                    consumed++;
                    maxRatio = Math.Max(maxRatio, ratio);
                }
                if (consumed == 0)
                {
                    break;
                }
                float ascent = Ascent * maxRatio;
                float descent = Descent * maxRatio;
                _lines.Add(new MeasuredLine(pos, consumed, width, ascent, descent, offsetY));
                offsetY += ascent + descent;
                pos += consumed;
            }
        }

        public int LineCount => _lines.Count;

        public MeasuredLine GetLine(int index) => _lines[index];

        public object? NativeLayout => null;

        public void Dispose()
        {
        }
    }

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
