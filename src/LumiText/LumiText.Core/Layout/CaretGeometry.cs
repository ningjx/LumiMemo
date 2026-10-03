using LumiText.Core.Editing;

namespace LumiText.Core.Layout;

/// <summary>
/// 光标几何（M2 §4.3）：从 <see cref="TextPosition"/> 正查文档坐标下的光标矩形。
/// </summary>
public readonly record struct CaretGeometry(float X, float Y, float Width, float Height)
{
    public LayoutRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// 光标/选区几何计算器（M2 §4.3/§4.4）：以 <see cref="LayoutResult"/> 为数据源，
/// 把 <see cref="TextPosition"/>/<see cref="TextRange"/> 翻译成文档坐标几何。
/// </summary>
public static class CaretGeometryCalculator
{
    /// <summary>光标默认宽（dip）。IME 组字期由上层加粗到 2px（§4.5）。</summary>
    public const float DefaultCaretWidth = 1.5f;

    /// <summary>
    /// 光标矩形：position 处的插入符（文档坐标）。
    /// position 越界（块索引/字符偏移超出排版产物）时钳到最近有效行盒；全文档无行盒返回 null。
    /// </summary>
    public static CaretGeometry? GetCaret(LayoutResult layout, TextPosition position)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Lines.Count == 0)
        {
            return null;
        }

        // 定位行盒：找「块索引 == position.BlockIndex 且 [CharStart, CharStart+CharCount) 含 position.CharIndex」的行；
        // 找不到时（块尾光标落在末行之后）钳到该块末行的行尾。
        // 无批占位行（空块的零字符行盒，Batch == null）单独截住：光标 = 行盒左上角 + 行高，
        // 不能落进批查询（line.Batch! 空引用）——空段落/空 bullet/空 todo 全靠这条路径显示光标。
        foreach (var candidate in layout.Lines)
        {
            if (candidate.BlockIndex != position.BlockIndex || candidate.Batch is not null)
            {
                continue;
            }
            return new CaretGeometry(
                candidate.X, candidate.Y, DefaultCaretWidth, candidate.Height);
        }

        PlacedLine? line = null;
        bool isLineEnd = false;
        foreach (var candidate in layout.Lines)
        {
            if (candidate.BlockIndex != position.BlockIndex || candidate.Batch is null)
            {
                continue;
            }
            int lineEnd = candidate.CharStart + candidate.CharCount;
            if (position.CharIndex >= candidate.CharStart && position.CharIndex < lineEnd)
            {
                line = candidate;
                isLineEnd = false;
                break;
            }
            if (position.CharIndex >= lineEnd)
            {
                line = candidate; // 候选：本块更靠后的行
                isLineEnd = true;
            }
        }
        line ??= layout.Lines[^1]; // 块索引越界：钳到全文末行

        // GetCaretGeometry 的 characterIndex 是批文本流内偏移（与 HitTestChar/GetCharRegions 同坐标系）：
        // 批的文本 = 块文本从 line.BatchStart 起的切片（绕图换段时批会在中途重建），
        // 批内偏移 = 块内偏移 − BatchStart；上界取「本行行尾的批内偏移」。
        int batchIndex = Math.Clamp(position.CharIndex - line.BatchStart, 0,
            line.CharStart + line.CharCount - line.BatchStart);
        var (x, yTop, height) = line.Batch!.GetCaretGeometry(batchIndex, isTrailing: isLineEnd);
        // 批布局坐标 → 文档坐标：行盒文档原点 + (批内偏移 − 行在批内的偏移)
        float docX = line.X + x;
        float docY = line.Y + (yTop - line.LineOffsetY);
        return new CaretGeometry(docX, docY, DefaultCaretWidth, height);
    }

    /// <summary>
    /// 选区几何：range 覆盖的所有矩形（文档坐标；每行一个，跨行自动拆分）。
    /// 空选区返回空列表；选区超出排版产物时钳到有效范围。
    /// </summary>
    public static IReadOnlyList<LayoutRect> GetSelectionRects(LayoutResult layout, TextRange range)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var result = new List<LayoutRect>();
        if (range.IsCollapsed || layout.Lines.Count == 0)
        {
            return result;
        }

        var start = range.Start;
        var end = range.End;

        foreach (var line in layout.Lines)
        {
            if (line.Batch is null)
            {
                continue;
            }
            if (line.BlockIndex < start.BlockIndex || line.BlockIndex > end.BlockIndex)
            {
                continue;
            }
            int lineStart = line.CharStart;
            int lineEnd = line.CharStart + line.CharCount;

            // 本行与选区的交集（块内偏移）
            int selStart = line.BlockIndex == start.BlockIndex
                ? Math.Max(start.CharIndex, lineStart)
                : lineStart;
            int selEnd = line.BlockIndex == end.BlockIndex
                ? Math.Min(end.CharIndex, lineEnd)
                : lineEnd;
            if (selStart >= selEnd)
            {
                continue;
            }

            // GetCharRegions 的 characterIndex 是批文本流内偏移（与 HitTestChar 同坐标系）：
            // 批内偏移 = 块内偏移 − BatchStart（绕图换段时批会在中途重建）
            int batchStart = selStart - line.BatchStart;
            int batchCount = selEnd - selStart;
            foreach (var region in line.Batch.GetCharRegions(batchStart, batchCount))
            {
                float docX = line.X + region.X;
                float docY = line.Y + (region.Y - line.LineOffsetY);
                result.Add(new LayoutRect(docX, docY, region.Width, region.Height));
            }
        }
        return result;
    }
}
