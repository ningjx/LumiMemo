namespace LumiMemo.Core.Search;

/// <summary>摘要里的一段文字，以及它是不是一处命中。</summary>
/// <remarks>
/// 展示层按 <see cref="IsMatch"/> 决定是否给这段文字加高亮背景；
/// 颜色由展示层决定（用便签自己的颜色），本层不认识颜色。
/// </remarks>
public sealed record SnippetSegment(string Text, bool IsMatch);
