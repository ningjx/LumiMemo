using System.Text.Json.Serialization;
using LumiText.Core.Documents.Serialization;

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
public abstract record Block
{
    /// <summary>
    /// 块级底色（Phase 3 §3.1）：null = 无背景。JSON 字段 "bg"（"#AARRGGBB"），
    /// 缺省不写出（WhenWritingDefault）——纯增量字段，旧文件读入为 null，不动 schema 版本。
    /// </summary>
    /// <remarks>
    /// 重建块时必须显式携带本字段（对象初始化器 `{ Background = 原块.Background }`）：
    /// 命令层大量经构造器重建块，漏带即丢背景——回归单测兜底（Phase 3 §3.4）。
    /// </remarks>
    [JsonPropertyName("bg")]
    [JsonConverter(typeof(Color32JsonConverter))]
    public Color32? Background { get; init; }
}
