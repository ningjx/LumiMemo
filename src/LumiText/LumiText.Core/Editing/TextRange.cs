namespace LumiText.Core.Editing;

/// <summary>
/// 选区：锚点 + 活动端。Anchor 是选区固定端（鼠标按下/Shift 起点），Active 是移动端。
/// 坍缩态（Anchor == Active）即光标。序无关语义经 <see cref="Start"/>/<see cref="End"/> 暴露。
/// </summary>
public readonly record struct TextRange(TextPosition Anchor, TextPosition Active)
{
    public bool IsCollapsed => Anchor == Active;

    public TextPosition Start => Anchor <= Active ? Anchor : Active;

    public TextPosition End => Anchor <= Active ? Active : Anchor;

    public static TextRange Collapse(TextPosition caret) => new(caret, caret);
}
