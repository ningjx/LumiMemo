namespace LumiMemo.Core.Services;

/// <summary>待办行的符号约定：行首 <c>☐ /☑ </c> 前缀的识别与文本变换。</summary>
/// <remarks>
/// <para>
/// RichEditBox 没有复选框列表样式（<c>MarkerType</c> 只有圆点、数字等，且不支持自定义符号），
/// 所以待办就是<strong>行首两个普通字符</strong>：未完成 <c>☐ </c>、已完成 <c>☑ </c>（U+2610/U+2611 + 空格）。
/// 编辑器负责把点击、Enter 接上去；这里只做纯文本变换，编辑器与标题派生（<see cref="TitleDeriver"/>）共用。
/// </para>
/// <para>
/// 前缀必须<strong>在行首</strong>才成立——行中间出现的 ☐ 只是普通字符，不该被当成待办。
/// </para>
/// </remarks>
public static class TodoMarkers
{
    /// <summary>未完成符号 ☐（U+2610）。</summary>
    public const char UncheckedSymbol = '☐';

    /// <summary>已完成符号 ☑（U+2611）。</summary>
    public const char CheckedSymbol = '☑';

    /// <summary>未完成前缀 <c>"☐ "</c>。</summary>
    public const string UncheckedPrefix = "☐ ";

    /// <summary>已完成前缀 <c>"☑ "</c>。</summary>
    public const string CheckedPrefix = "☑ ";

    /// <summary>前缀字符数（符号 + 一个空格）。</summary>
    public const int PrefixLength = 2;

    /// <summary>行首是否为待办前缀（☐ 或 ☑ + 空格）。</summary>
    public static bool IsTodoLine(string line) =>
        line.StartsWith(UncheckedPrefix, StringComparison.Ordinal) ||
        line.StartsWith(CheckedPrefix, StringComparison.Ordinal);

    /// <summary>是否为已完成行（<c>☑ </c> 开头）。</summary>
    public static bool IsCheckedLine(string line) =>
        line.StartsWith(CheckedPrefix, StringComparison.Ordinal);

    /// <summary>加未完成前缀；已是待办行则原样返回。</summary>
    public static string WithPrefix(string line) =>
        IsTodoLine(line) ? line : UncheckedPrefix + line;

    /// <summary>剥掉行首前缀；非待办行原样返回。</summary>
    public static string WithoutPrefix(string line) =>
        IsTodoLine(line) ? line[PrefixLength..] : line;

    /// <summary>行首符号切换 ☐↔☑；非待办行原样返回。</summary>
    public static string ToggleSymbol(string line) =>
        IsTodoLine(line)
            ? (IsCheckedLine(line) ? UncheckedPrefix : CheckedPrefix) + line[PrefixLength..]
            : line;

    /// <summary>按 Enter 时对当前行的动作。</summary>
    public static TodoEnterAction EnterAction(string line)
    {
        if (!IsTodoLine(line))
        {
            return TodoEnterAction.None;
        }

        // 只有前缀（或前缀加空白）的空待办行：退出待办，不再续行。
        return line[PrefixLength..].Trim().Length == 0
            ? TodoEnterAction.Exit
            : TodoEnterAction.Continue;
    }
}

/// <summary>Enter 落在待办行时的三种去向。</summary>
public enum TodoEnterAction
{
    /// <summary>不是待办行，按普通换行处理。</summary>
    None,

    /// <summary>续一条新待办：换行 + 新的 <c>☐ </c> 前缀。</summary>
    Continue,

    /// <summary>空待办行：去掉前缀退出待办，不换行。</summary>
    Exit,
}
