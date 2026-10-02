using System.Text.Json.Serialization;

namespace LumiText.Core.Documents;

/// <summary>
/// 文档块基类（.lumi v2 正文的多态根）。JSON 判别字段 "type"：
/// paragraph / heading / todo / divider / image（契约见 phase1-readonly-renderer-design.md §3.3）。
/// 新增块类型 = 新派生 record + 一枚 JsonDerivedType 标注，schema 版本随之演进。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(TodoBlock), "todo")]
[JsonDerivedType(typeof(DividerBlock), "divider")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record Block;
