namespace LumiText.Core.Layout;

/// <summary>
/// 块几何（Phase 3 §4）：块级装饰（背景）需要的整列矩形。
/// 只为「带 <see cref="Documents.Block.Background"/> 且产生行盒」的块产出，
/// 其余块零成本不产出。
/// </summary>
/// <param name="BlockIndex">块在文档块列表中的序号。</param>
/// <param name="Rect">X = 0、宽 = 内容区宽（整列口径，与 Word 段落底纹同构，不随缩进收窄）；
/// Y 上下各带内边距（按相邻块间距的一半钳制，防相邻块底色粘连/重叠）。</param>
public readonly record struct BlockExtent(int BlockIndex, LayoutRect Rect);
