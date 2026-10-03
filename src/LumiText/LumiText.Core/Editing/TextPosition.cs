namespace LumiText.Core.Editing;

/// <summary>
/// 文档内绝对位置（块索引 + 块内字符偏移）。块内偏移按 UTF-16 code unit 计（与 .NET string 一致），
/// 区间语义为「字符前间隙」：0 = 块首字符之前，PlainText.Length = 末字符之后。
/// </summary>
public readonly record struct TextPosition(int BlockIndex, int CharIndex) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other)
    {
        int block = BlockIndex.CompareTo(other.BlockIndex);
        return block != 0 ? block : CharIndex.CompareTo(other.CharIndex);
    }

    public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;
    public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;
    public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;
    public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;
}
