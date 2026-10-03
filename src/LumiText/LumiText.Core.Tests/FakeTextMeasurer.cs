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
        var chars = new char[length];
        int i = 0;
        foreach (var run in runs)
        {
            float ratio = run.Style?.FontSizeRatio ?? 1f;
            for (int j = 0; j < run.Text.Length; j++)
            {
                ratios[i] = ratio;
                chars[i] = run.Text[j];
                i++;
            }
        }
        return new FakeLineBatch(ratios, chars, maxWidth);
    }

    public LineHeightInfo MeasureLineHeight(TextStyle style) => new(Ascent, Descent);

    /// <summary>贪心填充 + 避头尾：逐字符累计字宽不超过段宽；一个字符都放不下时 LineCount = 0（窄段放弃信号）。</summary>
    /// <remarks>
    /// 避头尾（Phase 3 打磨）：与真实栈一致——断点若会让收尾标点（，。等）起行，
    /// 就把断点往前挪（把标点前一个字一起推到下一行）。见 <see cref="LineBreakRules"/>。
    /// </remarks>
    private sealed class FakeLineBatch : ILineBatch
    {
        private readonly List<MeasuredLine> _lines = new();

        public FakeLineBatch(float[] ratios, char[] chars, float maxWidth)
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

                // 避头尾：行末退字，直到下一行不是以收尾标点开头（全部退光则按原样，交由调用方放弃该段）
                int before = consumed;
                while (consumed > 0 && pos + consumed < chars.Length
                    && LineBreakRules.IsClosingPunctuation(chars[pos + consumed]))
                {
                    consumed--;
                }
                if (consumed == 0)
                {
                    consumed = before; // 退无可退：保持贪心结果，避免死循环（真栈此时也不会更差）
                }

                if (consumed == 0)
                {
                    break;
                }
                // 退字后重算宽度与行高（都要按实际容纳的字符算）
                width = 0f;
                maxRatio = 0f;
                for (int k = 0; k < consumed; k++)
                {
                    float ratio = ratios[pos + k];
                    width += CharWidth * ratio;
                    maxRatio = Math.Max(maxRatio, ratio);
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

        public CharHit? HitTestChar(float x, float y)
        {
            // 等宽假字体：y 定位行，x 定位字符（每字符 CharWidth × ratio，但测试只用 ratio=1）
            if (_lines.Count == 0)
            {
                return null;
            }
            int lineIndex = (int)(y / LineHeight);
            if (lineIndex < 0 || lineIndex >= _lines.Count)
            {
                return null;
            }
            var line = _lines[lineIndex];
            int charOffset = (int)(x / CharWidth);
            if (charOffset < 0 || charOffset >= line.CharsConsumed)
            {
                return null;
            }
            float charX = charOffset * CharWidth;
            bool isTrailing = x >= charX + CharWidth / 2f;
            return new CharHit(line.CharStart + charOffset, isTrailing);
        }

        public (float X, float YTop, float Height) GetCaretGeometry(int characterIndex, bool isTrailing)
        {
            if (_lines.Count == 0)
            {
                return (0f, 0f, 0f);
            }
            int clamped = Math.Clamp(characterIndex, 0,
                _lines[^1].CharStart + _lines[^1].CharsConsumed);
            // 找含该偏移的行：clamped == lineEnd 时优先下一行（isTrailing=false 的段首语义）；
            // isTrailing=true 或已到末行时，行尾偏移归本行
            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                int lineEnd = line.CharStart + line.CharsConsumed;
                bool isLast = i == _lines.Count - 1;
                if (clamped < lineEnd || (isLast && clamped <= lineEnd))
                {
                    // 与真实栈同语义：isTrailing 取「该字符右缘」而非左缘
                    // （恒定左缘会让「插入位置 k」的几何偏左一个字，探针查过的那类坑）
                    float x = isTrailing
                        ? Math.Min((clamped + 1 - line.CharStart) * CharWidth, line.Width)
                        : (clamped - line.CharStart) * CharWidth;
                    return (x, line.OffsetY, line.Ascent + line.Descent);
                }
            }
            var last = _lines[^1];
            return (last.Width, last.OffsetY, last.Ascent + last.Descent);
        }

        public IReadOnlyList<CharRegion> GetCharRegions(int characterIndex, int characterCount)
        {
            var result = new List<CharRegion>();
            int end = characterIndex + characterCount;
            foreach (var line in _lines)
            {
                int lineEnd = line.CharStart + line.CharsConsumed;
                int selStart = Math.Max(characterIndex, line.CharStart);
                int selEnd = Math.Min(end, lineEnd);
                if (selStart < selEnd)
                {
                    result.Add(new CharRegion(
                        selStart, selEnd - selStart,
                        (selStart - line.CharStart) * CharWidth, line.OffsetY,
                        (selEnd - selStart) * CharWidth, line.Ascent + line.Descent));
                }
            }
            return result;
        }

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
