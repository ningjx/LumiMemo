using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 一个文本段落：样式 run 序列 + 段落级样式。runs 拼接文本内不含换行符（换行即分段）。
/// </summary>
/// <remarks>
/// v2 演进（Phase 1 设计 §3.1）：<c>Text</c> 单串 → <see cref="Runs"/> 序列。
/// 保留 <see langword="string"/> 兼容构造与 <see cref="FromText"/>，
/// S2 的引擎/测试/Demo 现状调用点零改动；排版引擎在 M3 切换为消费 runs 的批量接口前，
/// 继续经 <see cref="PlainText"/> 取纯文本。
/// </remarks>
public sealed record ParagraphBlock : Block
{
    /// <summary>v2 主构造：样式 run 序列。</summary>
    [JsonConstructor]
    public ParagraphBlock(IReadOnlyList<TextRun> runs, TextStyle? style = null, float spaceAfter = 0f)
    {
        // JSON 缺省 runs 按缺省字段降级契约视为空段落（T-S2）。
        Runs = runs ?? [];
        Style = style;
        SpaceAfter = spaceAfter;
        PlainText = string.Concat(Runs.Select(static r => r.Text));
    }

    /// <summary>兼容构造：单一样式纯文本段落。</summary>
    public ParagraphBlock(string text, TextStyle? style = null, float spaceAfter = 0f)
        : this([new TextRun(text)], style, spaceAfter)
    {
    }

    /// <summary>文本流：runs 拼接即段落全文。</summary>
    [JsonPropertyName("runs")]
    public IReadOnlyList<TextRun> Runs { get; }

    /// <summary>段落样式；为 <see langword="null"/> 时使用 <see cref="TextStyle.Default"/>。</summary>
    [JsonPropertyName("style")]
    public TextStyle? Style { get; }

    /// <summary>段后间距（dip）。</summary>
    [JsonPropertyName("spaceAfter")]
    public float SpaceAfter { get; }

    /// <summary>runs 拼接后的纯文本（不含格式；M3 前引擎的输入形态）。</summary>
    [JsonIgnore]
    public string PlainText { get; }

    /// <summary>实际生效的段落样式。</summary>
    [JsonIgnore]
    public TextStyle EffectiveStyle => Style ?? TextStyle.Default;

    /// <summary>创建一个使用默认样式的纯文本段落。</summary>
    public static ParagraphBlock FromText(string text) => new(text);
}
