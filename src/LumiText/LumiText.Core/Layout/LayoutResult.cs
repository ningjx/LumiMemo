using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>命中测试结果（当前为行盒级：返回行首字符；字符级精确定位随编辑层在 Phase 2 引入）。</summary>
public readonly record struct HitTestResult(bool Found, int ParagraphIndex, int CharIndex);

/// <summary>
/// 一次完整排版的产物。实现 <see cref="IDisposable"/>：各行
/// <see cref="PlacedLine.NativeLayout"/> 的释放责任归本对象（去重后逐个 Dispose）。
/// </summary>
public sealed class LayoutResult : IDisposable
{
    public LayoutResult(IReadOnlyList<PlacedLine> lines, IReadOnlyList<FloatObject> floats, float totalHeight)
    {
        Lines = lines;
        Floats = floats;
        TotalHeight = totalHeight;
    }

    /// <summary>全部已放置行盒，按文档顺序（段落序 → 字符序）。</summary>
    public IReadOnlyList<PlacedLine> Lines { get; }

    /// <summary>参与本次排版的浮动对象（含最终位置）。</summary>
    public IReadOnlyList<FloatObject> Floats { get; }

    /// <summary>文档总高（内容底缘与浮动对象底缘的较大者）。</summary>
    public float TotalHeight { get; }

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
                return new HitTestResult(true, line.ParagraphIndex, line.CharStart);
            }
        }
        return default;
    }

    public void Dispose()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var line in Lines)
        {
            if (line.NativeLayout is IDisposable disposable && seen.Add(line.NativeLayout))
            {
                disposable.Dispose();
            }
        }
    }
}
