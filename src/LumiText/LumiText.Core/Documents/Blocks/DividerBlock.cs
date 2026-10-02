namespace LumiText.Core.Documents;

/// <summary>
/// 分割线块：无内容，排版层产出一个占位行盒（高度 = <see cref="TextStyle.Default"/> 空行高度），
/// 渲染层在占位行盒内画 1px 水平线（Phase 1 设计 §4/§6.2）。
/// </summary>
public sealed record DividerBlock : Block;
