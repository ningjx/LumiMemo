using LumiText.Core.Layout;

namespace LumiText.Core.Documents;

/// <summary>
/// 一篇文档（.lumi v2 的权威正文）：块列表 + 位图资源表。
/// 不可变 record 全家桶；编辑期的增量修改走「整块替换」（Phase 2 编辑层再引入差异结构）。
/// </summary>
public sealed record Document(IReadOnlyList<Block> Blocks, IReadOnlyList<ImageResource>? Images = null)
{
    /// <summary>正文结构自身的版本号，与 .lumi 文件级 Version 解耦（Phase 1 设计 §3.3）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// 从 <see cref="ImageBlock"/> 派生排版引擎的浮动输入（§3.1：Floats 是派生量，不存储）。
    /// 锚定浮动的 Rect 为占位（原点 + 显示尺寸），终位置由排版期两遍解析（§6.3）；
    /// 派生 <see cref="FloatObject.Id"/> = 所属块在 <see cref="Blocks"/> 中的索引。
    /// </summary>
    public IReadOnlyList<FloatObject> GetFloats()
    {
        var floats = new List<FloatObject>();
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i] is not ImageBlock { Float: { } placement } image)
            {
                continue;
            }
            var rect = placement.Anchor is not null
                ? new LayoutRect(0f, 0f, image.Width, image.Height)
                : new LayoutRect(placement.Position?.X ?? 0f, placement.Position?.Y ?? 0f,
                    image.Width, image.Height);
            floats.Add(new FloatObject(i, rect, placement.Side, placement.Margin)
            {
                Anchor = placement.Anchor,
            });
        }
        return floats;
    }
}
