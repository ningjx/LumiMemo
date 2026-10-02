namespace LumiText.Core.Documents;

/// <summary>
/// 段落级文本样式。当前迭代（S2）刻意只含字体族与字号：环绕排版算法
/// 与样式维度正交，多样式 run（粗/斜/色）属 Phase 1 的扩展点。
/// </summary>
public sealed record TextStyle(string FontFamily, float FontSize)
{
    /// <summary>库内默认样式：跟随系统的正文字体与 15dip 字号。</summary>
    public static TextStyle Default { get; } = new("Segoe UI", 15f);
}
