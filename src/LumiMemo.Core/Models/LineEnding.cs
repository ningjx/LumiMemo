namespace LumiMemo.Core.Models;

/// <summary>
/// 文件原有的行尾风格（§9.2、§5.9）。
/// </summary>
/// <remarks>
/// 写回时沿用原文件的行尾，不做规范化——否则用户的 git diff 会显示整个文件被改写，
/// 违反 §24.1 原则 3「对用户文件的最小侵入」。
/// </remarks>
public enum LineEnding
{
    /// <summary>仅换行符 LF（Unix 风格）。</summary>
    Lf,

    /// <summary>回车 + 换行 CRLF（Windows 风格）。新建便签使用这一种。</summary>
    CrLf,
}
