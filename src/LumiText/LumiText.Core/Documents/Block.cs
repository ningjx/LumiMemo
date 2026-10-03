using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 文档块基类（.lumi v2 正文的多态根）。JSON 判别字段 "type"：
/// paragraph / heading / todo / divider / image（契约见 phase1-readonly-renderer-design.md §3.3）。
/// 新增块类型 = 新派生 record + 一枚 JsonDerivedType 标注，schema 版本随之演进。
/// </summary>
/// <remarks>
/// 原先的块级底色字段（"bg"，Phase 3 M1）已在 Phase 3 打磨中移除——
/// 底色改为只作用于选中文字（<see cref="InlineStyle.Background"/>），块级不再有底色语义。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(TodoBlock), "todo")]
[JsonDerivedType(typeof(DividerBlock), "divider")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record Block;
