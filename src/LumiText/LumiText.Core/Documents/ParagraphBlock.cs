namespace LumiText.Core.Documents;

/// <summary>
/// 一个段落：纯文本 + 段落级样式。文本内不含换行符（换行即分段）。
/// </summary>
/// <param name="Text">段落文本，不允许包含 \r / \n。</param>
/// <param name="Style">段落样式；为 <see langword="null"/> 时使用 <see cref="TextStyle.Default"/>。</param>
/// <param name="SpaceAfter">段后间距（dip）。</param>
public sealed record ParagraphBlock(string Text, TextStyle? Style = null, float SpaceAfter = 0f)
{
    /// <summary>实际生效的样式。</summary>
    public TextStyle EffectiveStyle { get; } = Style ?? TextStyle.Default;

    /// <summary>创建一个使用默认样式的段落。</summary>
    public static ParagraphBlock FromText(string text) => new(text);
}
