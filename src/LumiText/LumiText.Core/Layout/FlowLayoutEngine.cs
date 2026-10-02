using LumiText.Core.Documents;

namespace LumiText.Core.Layout;

/// <summary>一次排版的批量统计（§10.3 基准与 T-B1 断言的数据源）。</summary>
/// <param name="BatchesCreated">经度量器创建的批数。</param>
/// <param name="BatchesDiscarded">脱离消费游标时仍有未消费行的批数（弃批：段切换/交集重探/交错段）。</param>
public readonly record struct LayoutStats(int BatchesCreated, int BatchesDiscarded);

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
/// M3 批量消费器（Phase 1 设计 §5.4）：段内维护单个「当前批 + 批内消费游标」，
/// 段（X，宽）不变时直接取批内下一行（零布局创建）；段变化或批耗尽时以
/// 「剩余文本 + 当前段宽」新建一批。已论证：任一时刻至多一个批有效
/// （批的下一未消费行须恰好落在当前文本位置，而这样的位置只有一个），
/// 因此单游标模型与按段缓存等价；交错段（一行横跨多段）自然退化为逐行建批——
/// 与旧首行探测同成本，正确性不受影响。
/// </para>
/// <para>
/// 线程模型：单次 <see cref="Layout"/> 调用自带状态（批游标），引擎实例本身不可共享并发排版；
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

    /// <summary>最近一次 <see cref="Layout"/> 的批量统计。</summary>
    public LayoutStats LastStats { get; private set; }

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
        int batchesCreated = 0;
        int batchesDiscarded = 0;

        float yCursor = 0f;
        for (int pi = 0; pi < paragraphs.Count; pi++)
        {
            var paragraph = paragraphs[pi];
            // M2 适配：ParagraphBlock 已演进为 runs 模型（v2）；
            // M3：批量接口直接消费 runs（行内样式由度量器应用）。
            string text = paragraph.PlainText;
            if (text.Length == 0)
            {
                // 空段落占一个空行高度。
                var empty = _measurer.MeasureLineHeight(paragraph.EffectiveStyle);
                yCursor += empty.Total;
            }
            else
            {
                var cursor = new BatchCursor();
                int consumed = 0;
                while (consumed < text.Length)
                {
                    consumed += LayoutRow(
                        paragraph.Runs, text.Length, consumed, paragraph.EffectiveStyle, pi,
                        bands, boundaries, contentWidth, ref yCursor, lines, cursor,
                        ref batchesCreated, ref batchesDiscarded);
                }
                // 段尾收尾：批未提交过行 → 引擎自行释放；已提交 → 所有权归 LayoutResult。
                DetachBatch(cursor, ref batchesDiscarded);
            }
            yCursor += paragraph.SpaceAfter;
        }

        float totalHeight = yCursor;
        foreach (var f in placed)
        {
            totalHeight = Math.Max(totalHeight, f.Rect.Bottom);
        }

        LastStats = new LayoutStats(batchesCreated, batchesDiscarded);
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

    // ------------------------------------------------------------------
    // 批量消费器（§5.4）
    // ------------------------------------------------------------------

    /// <summary>批复用键：段（X，宽）。仅宽度相同而 X 不同的两段不得共享一批（渲染分组原点依赖）。</summary>
    private readonly record struct SegmentKey(float X, float Width);

    /// <summary>段内批量消费游标：当前批 + 批内消费位置 + 批的绝对文本起点。</summary>
    private sealed class BatchCursor
    {
        public SegmentKey Key;
        public ILineBatch? Batch;
        public int NextLine;
        public int StartPos;
        public bool CommittedAny;
    }

    /// <summary>
    /// 取批内下一行（不提交）：段匹配且批未耗尽 → 零布局创建；否则以
    /// 「剩余文本 + 当前段宽」新建一批（旧批按弃批纪律释放）。
    /// 返回 <see langword="null"/> = 该段一行都放不下（窄段放弃信号）。
    /// </summary>
    private MeasuredLine? PeekLine(
        IReadOnlyList<TextRun> runs,
        int absPos,
        TextStyle style,
        HInterval segment,
        BatchCursor cursor,
        ref int batchesCreated,
        ref int batchesDiscarded)
    {
        var key = new SegmentKey(segment.X, segment.Width);
        if (cursor.Batch is null || !cursor.Key.Equals(key) || cursor.NextLine >= cursor.Batch.LineCount)
        {
            DetachBatch(cursor, ref batchesDiscarded);
            var batch = _measurer.LayoutLines(SliceRuns(runs, absPos), style, segment.Width);
            batchesCreated++;
            cursor.Key = key;
            cursor.Batch = batch;
            cursor.NextLine = 0;
            cursor.StartPos = absPos;
            cursor.CommittedAny = false;
        }

        if (cursor.Batch.LineCount == 0)
        {
            // 窄段：一行都放不下。批不留，由调用方顺延文本（Word 同款，防死循环）。
            DetachBatch(cursor, ref batchesDiscarded);
            return null;
        }
        return cursor.Batch.GetLine(cursor.NextLine);
    }

    /// <summary>批脱离游标时的统一处理：统计弃批；未提交过行的批由引擎释放（已提交的归 LayoutResult）。</summary>
    private static void DetachBatch(BatchCursor cursor, ref int batchesDiscarded)
    {
        if (cursor.Batch is null)
        {
            return;
        }
        if (cursor.NextLine < cursor.Batch.LineCount)
        {
            batchesDiscarded++;
        }
        if (!cursor.CommittedAny)
        {
            cursor.Batch.Dispose();
        }
        cursor.Batch = null;
    }

    /// <summary>截取「剩余文本」的 runs 视图（批量接口的输入）。</summary>
    private static IReadOnlyList<TextRun> SliceRuns(IReadOnlyList<TextRun> runs, int start)
    {
        if (start == 0)
        {
            return runs;
        }
        var sliced = new List<TextRun>();
        int pos = 0;
        foreach (var run in runs)
        {
            int runEnd = pos + run.Text.Length;
            if (runEnd > start)
            {
                int offset = Math.Max(0, start - pos);
                sliced.Add(new TextRun(run.Text[offset..], run.Style));
            }
            pos = runEnd;
        }
        return sliced;
    }

    /// <summary>
    /// 尝试在 <paramref name="yCursor"/> 处放置一行（可能横跨多个段）。
    /// 成功则提交行盒、推进 <paramref name="yCursor"/> 并返回本行消费的字符数；
    /// 当前 Y 放不下任何内容时，把 <paramref name="yCursor"/> 推进到下一个带边界并返回 0。
    /// </summary>
    private int LayoutRow(
        IReadOnlyList<TextRun> runs,
        int textLength,
        int start,
        TextStyle style,
        int paragraphIndex,
        IReadOnlyList<Band> bands,
        IReadOnlyList<float> boundaries,
        float contentWidth,
        ref float yCursor,
        List<PlacedLine> lines,
        BatchCursor cursor,
        ref int batchesCreated,
        ref int batchesDiscarded)
    {
        // 第一次探测：按 yCursor 所在带的段集合。
        var segments = SegmentsAt(bands, yCursor, contentWidth);
        var pending = ProbeRow(runs, textLength, start, style, segments, cursor,
            ref batchesCreated, ref batchesDiscarded, out float maxAscent, out float maxDescent);
        if (pending.Count == 0)
        {
            yCursor = NextBoundary(boundaries, yCursor);
            return 0;
        }

        // 行可能横跨多个带：取所跨各带段集合的交集（最窄约束），变化则重排一次。
        // 批量口径（§5.4 v2）：交集段宽与当前批不同 → PeekLine 自然弃批重建（计数进统计）。
        float lineHeight = maxAscent + maxDescent;
        var exact = SegmentsSpanning(bands, yCursor, yCursor + lineHeight, contentWidth);
        if (!SameSegments(segments, exact))
        {
            pending = ProbeRow(runs, textLength, start, style, exact, cursor,
                ref batchesCreated, ref batchesDiscarded, out maxAscent, out maxDescent);
            if (pending.Count == 0 || Math.Abs((maxAscent + maxDescent) - lineHeight) > Epsilon)
            {
                // 交集后放不下，或行高变化导致约束再次改变（罕见）：放弃本 Y，推进。
                yCursor = NextBoundary(boundaries, yCursor);
                return 0;
            }
        }

        // 提交：同带多段共享基线（"文字在图片两侧同一行对齐"）。
        float baseline = yCursor + maxAscent;
        int rowConsumed = 0;
        foreach (var (segment, line, batch, lineIndex, charStart) in pending)
        {
            lines.Add(new PlacedLine(
                paragraphIndex,
                charStart,
                line.CharsConsumed,
                segment.X,
                baseline - line.Ascent,
                line.Width,
                line.Ascent + line.Descent,
                baseline,
                batch,
                line.OffsetY));
            rowConsumed += line.CharsConsumed;
            // 提交游标：仅当行来自当前批且确为下一未消费行（交错段的早段批已被替换，无需推进）。
            if (ReferenceEquals(batch, cursor.Batch) && lineIndex == cursor.NextLine)
            {
                cursor.NextLine++;
                cursor.CommittedAny = true;
            }
        }
        yCursor += lineHeight;
        return rowConsumed;
    }

    /// <summary>逐段探测一行：按 X 序填充各段，段间顺序消费文本（只看不取，提交在 LayoutRow）。</summary>
    private List<(HInterval Segment, MeasuredLine Line, ILineBatch Batch, int LineIndex, int CharStart)> ProbeRow(
        IReadOnlyList<TextRun> runs,
        int textLength,
        int start,
        TextStyle style,
        IReadOnlyList<HInterval> segments,
        BatchCursor cursor,
        ref int batchesCreated,
        ref int batchesDiscarded,
        out float maxAscent,
        out float maxDescent)
    {
        var pending = new List<(HInterval, MeasuredLine, ILineBatch, int, int)>();
        maxAscent = 0f;
        maxDescent = 0f;
        int absPos = start;
        foreach (var segment in segments)
        {
            if (absPos >= textLength)
            {
                break;
            }
            if (segment.Width < 1f)
            {
                continue;
            }
            var line = PeekLine(runs, absPos, style, segment, cursor,
                ref batchesCreated, ref batchesDiscarded);
            if (line is not { CharsConsumed: > 0 } found)
            {
                // 窄段放弃：文本顺延到下一个有空间的段/带（Word 同款，防死循环的关键）。
                continue;
            }
            pending.Add((segment, found, cursor.Batch!, cursor.NextLine, absPos));
            absPos += found.CharsConsumed;
            maxAscent = Math.Max(maxAscent, found.Ascent);
            maxDescent = Math.Max(maxDescent, found.Descent);
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
