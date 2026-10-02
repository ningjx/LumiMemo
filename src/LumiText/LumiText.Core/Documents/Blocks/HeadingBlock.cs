using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 标题块 H1–H3：本质是「字号预设的段落」（Phase 1 设计 §3.1）——
/// 排版层不需要为它新增块类型分支，只按 <see cref="EffectiveStyle"/> 的字号排版。
/// </summary>
public sealed record HeadingBlock : Block
{
    [JsonConstructor]
    public HeadingBlock(IReadOnlyList<TextRun> runs, int level, float spaceAfter = 0f)
    {
        if (level is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "标题层级只支持 1–3。");
        }
        // JSON 缺省 runs 按缺省字段降级契约视为空标题（T-S2）。
        Runs = runs ?? [];
        Level = level;
        SpaceAfter = spaceAfter;
        PlainText = string.Concat(Runs.Select(static r => r.Text));
    }

    /// <summary>兼容构造：单一样式纯文本标题。</summary>
    public HeadingBlock(string text, int level, float spaceAfter = 0f)
        : this([new TextRun(text)], level, spaceAfter)
    {
    }

    [JsonPropertyName("runs")]
    public IReadOnlyList<TextRun> Runs { get; }

    /// <summary>标题层级 1–3。</summary>
    [JsonPropertyName("level")]
    public int Level { get; }

    [JsonPropertyName("spaceAfter")]
    public float SpaceAfter { get; }

    [JsonIgnore]
    public string PlainText { get; }

    /// <summary>字号预设（O1，2026-10-02 确认）：H1/H2/H3 = 22/18/16 dip，字体继承默认。</summary>
    [JsonIgnore]
    public TextStyle EffectiveStyle => new(TextStyle.Default.FontFamily, Level switch
    {
        1 => 22f,
        2 => 18f,
        _ => 16f,
    });
}
