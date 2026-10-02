using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>命中测试结果（当前为行盒级：返回行首字符；字符级精确定位随编辑层在 Phase 2 引入）。</summary>
public readonly record struct HitTestResult(bool Found, int BlockIndex, int CharIndex);

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

    /// <summary>坐标命中：命中文本行盒。</summary>
    public HitTestResult HitTest(float x, float y)
    {
        foreach (var line in Lines)
        {
            if (line.Bounds.Contains(x, y))
            {
                return new HitTestResult(true, line.BlockIndex, line.CharStart);
            }
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
