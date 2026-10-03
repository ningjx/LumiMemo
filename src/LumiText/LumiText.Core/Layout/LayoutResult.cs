using LumiText.Core.Documents;
using LumiText.Core.Editing;

namespace LumiText.Core.Layout;

/// <summary>
/// 命中测试结果（M2 字符级）：命中的行 + 块内字符偏移 + 落尾标记。
/// <see cref="CaretPosition"/> 把命中结果直接翻译成编辑器可用的光标位置。
/// </summary>
public readonly record struct HitTestResult(
    bool Found,
    int BlockIndex,
    int CharIndex,
    bool IsTrailingHit)
{
    /// <summary>命中点对应的光标位置（落尾时落在字符之后）。</summary>
    public TextPosition CaretPosition => new(BlockIndex, CharIndex);
}

/// <summary>
/// 一次完整排版的产物。实现 <see cref="IDisposable"/>：各行引用的
/// <see cref="PlacedLine.Batch"/> 的释放责任归本对象（按引用去重后逐个 Dispose，§5.2 所有权契约）。
/// </summary>
public sealed class LayoutResult : IDisposable
{
    public LayoutResult(
        IReadOnlyList<PlacedLine> lines,
        IReadOnlyList<FloatObject> floats,
        float totalHeight,
        IReadOnlyList<Block>? blocks = null)
    {
        Lines = lines;
        Floats = floats;
        TotalHeight = totalHeight;
        Blocks = blocks;
    }

    /// <summary>全部已放置行盒，按文档顺序（块序 → 字符序）。</summary>
    public IReadOnlyList<PlacedLine> Lines { get; }

    /// <summary>参与本次排版的浮动对象（含最终位置）。</summary>
    public IReadOnlyList<FloatObject> Floats { get; }

    /// <summary>文档总高（内容底缘与浮动对象底缘的较大者）。</summary>
    public float TotalHeight { get; }

    /// <summary>
    /// 源块列表（只读透传，M4）：渲染层画 Todo 复选框需要 <c>TodoBlock.Checked</c>、
    /// 分派块级绘制路径需要块类型（Phase 1 设计 §4——排版产物自身不带块元数据会让渲染层无路可查）。
    /// 经旧签名（段落列表）排版时为 <see langword="null"/>。
    /// </summary>
    public IReadOnlyList<Block>? Blocks { get; }

    /// <summary>坐标命中：命中最上层浮动对象（用于拖动/手柄命中）。</summary>
    public FloatObject? FloatAt(float x, float y)
    {
        for (int i = Floats.Count - 1; i >= 0; i--)
        {
            if (Floats[i].Rect.Contains(x, y))
            {
                return Floats[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 坐标命中：字符级命中（M2）。
    /// 先定位行盒，再经行盒所属批的 <see cref="ILineBatch.HitTestChar"/> 精确到字符偏移；
    /// 批不支持字符级命中（如 Divider 占位行盒无批）时退化为行首字符（M1 行为）。
    /// </summary>
    public HitTestResult HitTest(float x, float y)
    {
        foreach (var line in Lines)
        {
            if (!line.Bounds.Contains(x, y))
            {
                continue;
            }
            if (line.Batch is null)
            {
                // 无批占位行盒（空块/空 bullet/空 todo）：命中即块内唯一光标位（0,0）。
                // 空段落原本没有行盒、点击不响应——这条路径让它获得光标落点。
                return new HitTestResult(true, line.BlockIndex, line.CharStart, false);
            }
            // 批布局坐标 = (点 X − 行盒 X, 点 Y − 行盒 Y + 行在批内的偏移)
            float localX = x - line.X;
            float localY = (y - line.Y) + line.LineOffsetY;
            var hit = line.Batch.HitTestChar(localX, localY);
            if (hit is not { } h)
            {
                return new HitTestResult(true, line.BlockIndex, line.CharStart, false);
            }
            // HitTestChar 返回批文本流内偏移（与 GetCaretGeometry/GetCharRegions 同坐标系）；
            // 块内偏移 = 批内偏移（批的文本起点 == 行的 CharStart 由排版引擎保证）
            int charIndex = h.CharacterIndex + (h.IsTrailingHit ? 1 : 0);
            return new HitTestResult(true, line.BlockIndex, charIndex, h.IsTrailingHit);
        }
        return default;
    }

    public void Dispose()
    {
        var seen = new HashSet<ILineBatch>(ReferenceEqualityComparer.Instance);
        foreach (var line in Lines)
        {
            if (line.Batch is not null && seen.Add(line.Batch))
            {
                line.Batch.Dispose();
            }
        }
    }
}
