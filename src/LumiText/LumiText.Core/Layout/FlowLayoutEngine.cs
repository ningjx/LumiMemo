using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>
/// 浮动环绕排版引擎：Band（垂直带）+ 行盒分割算法。
/// </summary>
/// <remarks>
/// <para>
/// 算法概要：所有浮动对象的顶/底缘把文档纵向切成若干"带"，带内排除关系恒定；
/// 每个带的可用水平区间 = 内容宽度减去相交浮动矩形的 margin 外扩区，得到 0~N 个"段"。
/// 逐段落、逐带地按段填充行盒；一行横跨多个带时，取所跨各带段集合的交集
/// （最窄约束）重排一次，使"矮图片只挤占相交的行"（Word 同款行为）。
/// 同一带内多个段（图片两侧）的行盒共享基线。文字随浮动位置变化自动"飞到另一侧"，
/// 无需任何方向特判。
/// </para>
/// <para>
/// 线程模型：无状态（度量器除外），单次 <see cref="Layout"/> 调用纯计算；
/// 调用方负责 UI 线程亲和与增量失效策略。
/// </para>
/// </remarks>
public sealed class FlowLayoutEngine
{
    /// <summary>浮点比较容差（dip）。浮动边界与行边界"恰好重合"时避免 1px 缝隙或重叠。</summary>
    private const float Epsilon = 0.01f;

    private readonly ITextMeasurer _measurer;

    public FlowLayoutEngine(ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        _measurer = measurer;
    }

    /// <summary>对整篇文档做一次全量排版。</summary>
    /// <param name="paragraphs">段落序列（不允许含换行符）。</param>
    /// <param name="floats">浮动对象（文档坐标矩形）。</param>
    /// <param name="contentWidth">内容区宽度（dip）。</param>
    /// <remarks>
    /// 浮动矩形先经 <see cref="PlaceFloats"/> 归一化进内容框（窗口收窄时把溢出的浮动拉回，
    /// 宽于内容区的只贴左缘）；归一化只作用于本次排版结果，调用方持有的原始位置不变，
    /// 因此窗口恢复宽度后浮动回到作者原位。
    /// </remarks>
    public LayoutResult Layout(
        IReadOnlyList<ParagraphBlock> paragraphs,
        IReadOnlyList<FloatObject> floats,
        float contentWidth)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        ArgumentNullException.ThrowIfNull(floats);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(contentWidth, 0f);

        var placed = PlaceFloats(floats, contentWidth);
        var bands = BuildBands(placed, contentWidth);
        var boundaries = bands.Select(b => b.YBottom).ToArray();
        var lines = new List<PlacedLine>();

        float yCursor = 0f;
        for (int pi = 0; pi < paragraphs.Count; pi++)
        {
            var paragraph = paragraphs[pi];
            if (paragraph.Text.Length == 0)
            {
                // 空段落占一个空行高度。
                var empty = _measurer.MeasureLineHeight(paragraph.EffectiveStyle);
                yCursor += empty.Total;
            }
            else
            {
                int consumed = 0;
                while (consumed < paragraph.Text.Length)
                {
                    consumed += LayoutRow(
                        paragraph.Text, consumed, paragraph.EffectiveStyle, pi,
                        bands, boundaries, contentWidth, ref yCursor, lines);
                }
            }
            yCursor += paragraph.SpaceAfter;
        }

        float totalHeight = yCursor;
        foreach (var f in placed)
        {
            totalHeight = Math.Max(totalHeight, f.Rect.Bottom);
        }

        return new LayoutResult(lines, placed, totalHeight);
    }

    /// <summary>把浮动矩形归一化进内容框：X 限制在 [0, 内容宽 − 矩形宽]、Y 不小于 0。</summary>
    /// <remarks>
    /// 矩形宽于内容区时只贴左缘（不做缩放——图片适配属于渲染层，见 Phase 1 图片对象）。
    /// </remarks>
    private static IReadOnlyList<FloatObject> PlaceFloats(
        IReadOnlyList<FloatObject> floats, float contentWidth)
    {
        if (floats.Count == 0)
        {
            return floats;
        }

        var placed = new FloatObject[floats.Count];
        for (int i = 0; i < floats.Count; i++)
        {
            var f = floats[i];
            float x = Math.Clamp(f.Rect.X, 0f, Math.Max(0f, contentWidth - f.Rect.Width));
            float y = Math.Max(0f, f.Rect.Y);
            placed[i] = x == f.Rect.X && y == f.Rect.Y ? f : f.MovedTo(x, y);
        }
        return placed;
    }

    /// <summary>
    /// 尝试在 <paramref name="yCursor"/> 处放置一行（可能横跨多个段）。
    /// 成功则提交行盒、推进 <paramref name="yCursor"/> 并返回本行消费的字符数；
    /// 当前 Y 放不下任何内容时，把 <paramref name="yCursor"/> 推进到下一个带边界并返回 0。
    /// </summary>
    private int LayoutRow(
        string text,
        int start,
        TextStyle style,
        int paragraphIndex,
        IReadOnlyList<Band> bands,
        IReadOnlyList<float> boundaries,
        float contentWidth,
        ref float yCursor,
        List<PlacedLine> lines)
    {
        string remaining = text[start..];

        // 第一次探测：按 yCursor 所在带的段集合。
        var segments = SegmentsAt(bands, yCursor, contentWidth);
        var pending = ProbeRow(remaining, style, segments, start, out float maxAscent, out float maxDescent);
        if (pending.Count == 0)
        {
            yCursor = NextBoundary(boundaries, yCursor);
            return 0;
        }

        // 行可能横跨多个带：取所跨各带段集合的交集（最窄约束），变化则重排一次。
        float lineHeight = maxAscent + maxDescent;
        var exact = SegmentsSpanning(bands, yCursor, yCursor + lineHeight, contentWidth);
        if (!SameSegments(segments, exact))
        {
            DiscardNativeLayouts(pending);
            pending = ProbeRow(remaining, style, exact, start, out maxAscent, out maxDescent);
            if (pending.Count == 0 || Math.Abs((maxAscent + maxDescent) - lineHeight) > Epsilon)
            {
                // 交集后放不下，或行高变化导致约束再次改变（罕见）：放弃本 Y，推进。
                DiscardNativeLayouts(pending);
                yCursor = NextBoundary(boundaries, yCursor);
                return 0;
            }
        }

        // 提交：同带多段共享基线（"文字在图片两侧同一行对齐"）。
        float baseline = yCursor + maxAscent;
        int rowConsumed = 0;
        foreach (var (segment, info, charStart) in pending)
        {
            lines.Add(new PlacedLine(
                paragraphIndex,
                charStart,
                info.CharsConsumed,
                segment.X,
                baseline - info.Ascent,
                info.Width,
                info.Ascent + info.Descent,
                baseline,
                info.NativeLayout));
            rowConsumed += info.CharsConsumed;
        }
        yCursor += lineHeight;
        return rowConsumed;
    }

    /// <summary>逐段探测一行：按 X 序填充各段，段间顺序消费文本。</summary>
    private List<(HInterval Segment, FirstLineInfo Info, int CharStart)> ProbeRow(
        string remaining,
        TextStyle style,
        IReadOnlyList<HInterval> segments,
        int charStartBase,
        out float maxAscent,
        out float maxDescent)
    {
        var pending = new List<(HInterval, FirstLineInfo, int)>();
        maxAscent = 0f;
        maxDescent = 0f;
        int rowConsumed = 0;
        foreach (var segment in segments)
        {
            if (rowConsumed >= remaining.Length)
            {
                break;
            }
            if (segment.Width < 1f)
            {
                continue;
            }
            var info = _measurer.LayoutFirstLine(remaining[rowConsumed..], style, segment.Width);
            if (info.CharsConsumed <= 0)
            {
                // 窄段放弃：文本顺延到下一个有空间的段/带（Word 同款，防死循环的关键）。
                continue;
            }
            pending.Add((segment, info, charStartBase + rowConsumed));
            rowConsumed += info.CharsConsumed;
            maxAscent = Math.Max(maxAscent, info.Ascent);
            maxDescent = Math.Max(maxDescent, info.Descent);
        }
        return pending;
    }

    // ------------------------------------------------------------------
    // 带与段的几何计算
    // ------------------------------------------------------------------

    /// <summary>水平区间（段）。</summary>
    private readonly record struct HInterval(float X, float Right)
    {
        public float Width => Right - X;
    }

    private readonly record struct Band(float YTop, float YBottom, IReadOnlyList<HInterval> Segments);

    private static List<Band> BuildBands(IReadOnlyList<FloatObject> floats, float contentWidth)
    {
        var cuts = new SortedSet<float> { 0f };
        foreach (var f in floats)
        {
            cuts.Add(Math.Max(0f, f.Rect.Y));
            cuts.Add(Math.Max(0f, f.Rect.Bottom));
        }
        cuts.Add(float.MaxValue);

        var boundaries = cuts.ToArray();
        var bands = new List<Band>(boundaries.Length - 1);
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            float yTop = boundaries[i];
            float yBottom = boundaries[i + 1];
            bands.Add(new Band(yTop, yBottom, ComputeSegments(floats, contentWidth, yTop, yBottom)));
        }
        return bands;
    }

    /// <summary>带内可用段：[0, W] 减去所有与该带相交的浮动矩形（margin 外扩）。</summary>
    private static List<HInterval> ComputeSegments(
        IReadOnlyList<FloatObject> floats, float contentWidth, float yTop, float yBottom)
    {
        var segments = new List<HInterval> { new(0f, contentWidth) };
        foreach (var f in floats)
        {
            var rect = f.Rect;
            if (!rect.IntersectsVertically(yTop + Epsilon, yBottom - Epsilon))
            {
                continue;
            }
            float cutLeft = Math.Max(0f, rect.X - f.Margin);
            float cutRight = Math.Min(contentWidth, rect.Right + f.Margin);
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var seg = segments[i];
                if (cutRight <= seg.X + Epsilon || cutLeft >= seg.Right - Epsilon)
                {
                    continue;
                }
                segments.RemoveAt(i);
                if (cutLeft > seg.X + Epsilon)
                {
                    segments.Insert(i, new HInterval(seg.X, cutLeft));
                    i++;
                }
                if (cutRight < seg.Right - Epsilon)
                {
                    segments.Insert(i, new HInterval(cutRight, seg.Right));
                }
                i--;
            }
        }
        segments.Sort((a, b) => a.X.CompareTo(b.X));
        return segments;
    }

    private static IReadOnlyList<HInterval> SegmentsAt(
        IReadOnlyList<Band> bands, float y, float contentWidth)
    {
        foreach (var band in bands)
        {
            if (y < band.YBottom - Epsilon)
            {
                return band.Segments;
            }
        }
        return new List<HInterval> { new(0f, contentWidth) };
    }

    /// <summary>[yTop, yBottom) 所跨各带段集合的交集。</summary>
    private static IReadOnlyList<HInterval> SegmentsSpanning(
        IReadOnlyList<Band> bands, float yTop, float yBottom, float contentWidth)
    {
        List<HInterval>? result = null;
        foreach (var band in bands)
        {
            if (band.YBottom <= yTop + Epsilon || band.YTop >= yBottom - Epsilon)
            {
                continue;
            }
            result = result is null
                ? new List<HInterval>(band.Segments)
                : Intersect(result, band.Segments);
        }
        return result ?? new List<HInterval> { new(0f, contentWidth) };
    }

    private static List<HInterval> Intersect(List<HInterval> a, IReadOnlyList<HInterval> b)
    {
        var result = new List<HInterval>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                float left = Math.Max(x.X, y.X);
                float right = Math.Min(x.Right, y.Right);
                if (right > left + Epsilon)
                {
                    result.Add(new HInterval(left, right));
                }
            }
        }
        return result;
    }

    private static bool SameSegments(IReadOnlyList<HInterval> a, IReadOnlyList<HInterval> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > Epsilon || Math.Abs(a[i].Right - b[i].Right) > Epsilon)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>探测结果被放弃时释放随行的度量器私有布局，避免泄漏（契约见 ITextMeasurer）。</summary>
    private static void DiscardNativeLayouts(List<(HInterval Segment, FirstLineInfo Info, int CharStart)> pending)
    {
        foreach (var (_, info, _) in pending)
        {
            if (info.NativeLayout is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>大于 y 的最近带边界（恒存在：最后一条边界是 float.MaxValue）。</summary>
    private static float NextBoundary(IReadOnlyList<float> boundaries, float y)
    {
        foreach (var b in boundaries)
        {
            if (b > y + Epsilon)
            {
                return b;
            }
        }
        return y + 1f; // 不可达（MaxValue 恒在），防御性返回
    }
}
