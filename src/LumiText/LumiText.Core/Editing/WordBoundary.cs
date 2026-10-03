namespace LumiText.Core.Editing;

/// <summary>
/// 词边界判定（双击选词用）：Unicode 字母/数字为词字符，CJK 表意文字按单字成词
/// （无空格分词的语言，词级粒度退化为字符级，与 Win32 RichEdit 行为一致）。
/// </summary>
public static class WordBoundary
{
    /// <summary>
    /// 在 text 内展开包含 position 的词区间 [start, end)。
    /// position 落在词字符上 → 向两侧扩展到非词字符；落在非词字符（标点/空格）上 →
    /// 选中该字符本身。position 越界按最近合法位置处理。空文本返回 (0, 0)。
    /// </summary>
    public static (int Start, int End) Expand(string text, int position)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return (0, 0);
        }
        int pos = Math.Clamp(position, 0, text.Length - 1);

        if (!IsWordChar(text[pos]))
        {
            return (pos, pos + 1);
        }

        int start = pos;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        int end = pos + 1;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }
        return (start, end);
    }

    /// <summary>词字符 = Unicode 字母/数字，或 CJK 表意文字（含兼容表意文字区）。</summary>
    public static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c))
        {
            return true;
        }
        return IsCjkIdeograph(c);
    }

    private static bool IsCjkIdeograph(char c)
    {
        // U+4E00..U+9FFF CJK Unified / U+3400..U+4DBF Ext-A / U+F900..U+FAFF 兼容表意
        int v = c;
        return (v >= 0x4E00 && v <= 0x9FFF)
            || (v >= 0x3400 && v <= 0x4DBF)
            || (v >= 0xF900 && v <= 0xFAFF);
    }
}
