namespace LumiText.Core.Layout;

/// <summary>
/// 中文断行的避头尾规则（Phase 3 打磨抽出的单一事实源）：哪些字符<b>不允许出现在行首</b>。
/// </summary>
/// <remarks>
/// <para>
/// 真栈里由 DirectWrite/Win2D 的断行器执行：断点若会让这些字符起行，就把断点往前挪一个
/// （表现为「最后一个字被一起推到下一行」）。假字体的 <c>FakeTextMeasurer</c> 按同一规则如实实现，
/// 否则引擎里依赖它的逻辑在单测里永远触发不到（见 §8 打磨记录）。
/// </para>
/// <para>
/// 引擎只在<b>分段边界</b>上放宽这条规则（图片另一侧的同视觉行不算"行首"，
/// 见 <c>FlowLayoutEngine</c> 的段边界补字）：正文换行照旧守避头尾。
/// </para>
/// </remarks>
public static class LineBreakRules
{
    /// <summary>行首禁则字符集（中文收尾标点 + 收尾括号引号 + 常见西文收尾）。</summary>
    public const string ClosingPunctuation = "，。、；：！？）】》」』〕〉·…～”’!?,.;:)]}";

    /// <summary>该字符是否不允许出现在行首。</summary>
    public static bool IsClosingPunctuation(char ch) => ClosingPunctuation.Contains(ch);
}
