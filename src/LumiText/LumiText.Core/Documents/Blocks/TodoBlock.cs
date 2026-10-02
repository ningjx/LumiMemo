using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 待办块：带悬挂缩进的段落（Phase 1 设计 §3.1/O2）——文本整体右移一个复选框宽度，
/// 复选框由渲染层画在缩进区里，不进文本流；勾选态只影响渲染，不影响排版流。
/// </summary>
public sealed record TodoBlock : Block
{
    [JsonConstructor]
    public TodoBlock(IReadOnlyList<TextRun> runs, bool @checked = false, float spaceAfter = 0f)
    {
        // JSON 缺省 runs 按缺省字段降级契约视为空待办（T-S2）。
        Runs = runs ?? [];
        Checked = @checked;
        SpaceAfter = spaceAfter;
        PlainText = string.Concat(Runs.Select(static r => r.Text));
    }

    /// <summary>兼容构造：单一样式纯文本待办。</summary>
    public TodoBlock(string text, bool @checked = false, float spaceAfter = 0f)
        : this([new TextRun(text)], @checked, spaceAfter)
    {
    }

    [JsonPropertyName("runs")]
    public IReadOnlyList<TextRun> Runs { get; }

    [JsonPropertyName("checked")]
    public bool Checked { get; }

    [JsonPropertyName("spaceAfter")]
    public float SpaceAfter { get; }

    [JsonIgnore]
    public string PlainText { get; }

    /// <summary>悬挂缩进（dip）：复选框 20 + 间隙 6（Phase 1 设计 §4 建议值）。</summary>
    [JsonIgnore]
    public float LeftIndent => 26f;

    [JsonIgnore]
    public TextStyle EffectiveStyle => TextStyle.Default;
}
