using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.Core.Tests;

/// <summary>
/// T-B2 的逐行参照实现：S2 旧引擎（首行探测版）的原样复制，仅用于断言
/// 「批量消费器结果与逐行旧逻辑逐行一致」（Phase 1 设计 §10.1 T-B2）。
/// 生产代码不再使用此路径；几何部分与引擎同源（Band + 行盒分割）。
/// </summary>
internal static class ReferenceLayout
{
    private const float Epsilon = 0.01f;

    public readonly record struct RefLine(
        int ParagraphIndex, int CharStart, int CharCount,
        float X, float Y, float Width, float Baseline);

    public static List<RefLine> Layout(
        IReadOnlyList<ParagraphBlock> paragraphs,
        IReadOnlyList<FloatObject> floats,
        float contentWidth)
    {
        var placed = PlaceFloats(floats, contentWidth);
        var bands = BuildBands(placed, contentWidth);
        var boundaries = bands.Select(b => b.YBottom).ToArray();
        var lines = new List<RefLine>();

        float yCursor = 0f;
        for (int pi = 0; pi < paragraphs.Count; pi++)
        {
            var paragraph = paragraphs[pi];
            string text = paragraph.PlainText;
            if (text.Length == 0)
            {
                yCursor += FakeTextMeasurer.LineHeight;
            }
            else
            {
                int consumed = 0;
                while (consumed < text.Length)
                {
                    consumed += LayoutRow(text, consumed, pi, bands, boundaries, contentWidth, ref yCursor, lines);
                }
            }
            yCursor += paragraph.SpaceAfter;
        }
        return lines;
    }

    private static int LayoutRow(
        string text,
        int start,
        int paragraphIndex,
        IReadOnlyList<Band> bands,
        IReadOnlyList<float> boundaries,
        float contentWidth,
        ref float yCursor,
        List<RefLine> lines)
    {
        string remaining = text[start..];
        var segments = SegmentsAt(bands, yCursor, contentWidth);
        var pending = ProbeRow(remaining, segments, start, out float maxAscent, out float maxDescent);
        if (pending.Count == 0)
        {
            yCursor = NextBoundary(boundaries, yCursor);
            return 0;
        }

        float lineHeight = maxAscent + maxDescent;
        var exact = SegmentsSpanning(bands, yCursor, yCursor + lineHeight, contentWidth);
        if (!SameSegments(segments, exact))
        {
            pending = ProbeRow(remaining, exact, start, out maxAscent, out maxDescent);
            if (pending.Count == 0 || Math.Abs((maxAscent + maxDescent) - lineHeight) > Epsilon)
            {
                yCursor = NextBoundary(boundaries, yCursor);
                return 0;
            }
        }

        float baseline = yCursor + maxAscent;
        int rowConsumed = 0;
        foreach (var (segment, info, charStart) in pending)
        {
            lines.Add(new RefLine(
                paragraphIndex, charStart, info.CharsConsumed,
                segment.X, baseline - info.Ascent, info.Width, baseline));
            rowConsumed += info.CharsConsumed;
        }
        yCursor += lineHeight;
        return rowConsumed;
    }

    private readonly record struct FirstLine(int CharsConsumed, float Width, float Ascent, float Descent);

    /// <summary>与旧 FakeTextMeasurer 同规则的首行探测（ratio = 1 场景专用）。</summary>
    private static FirstLine LayoutFirstLine(string text, float maxWidth)
    {
        int fit = (int)(maxWidth / FakeTextMeasurer.CharWidth);
        int consumed = Math.Min(text.Length, fit);
        return consumed <= 0
            ? default
            : new FirstLine(consumed, consumed * FakeTextMeasurer.CharWidth,
                FakeTextMeasurer.Ascent, FakeTextMeasurer.Descent);
    }

    private static List<(HInterval Segment, FirstLine Info, int CharStart)> ProbeRow(
        string remaining,
        IReadOnlyList<HInterval> segments,
        int charStartBase,
        out float maxAscent,
        out float maxDescent)
    {
        var pending = new List<(HInterval, FirstLine, int)>();
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
            var info = LayoutFirstLine(remaining[rowConsumed..], segment.Width);
            if (info.CharsConsumed <= 0)
            {
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
    // 几何（与引擎同源的原样复制）
    // ------------------------------------------------------------------

    private readonly record struct HInterval(float X, float Right)
    {
        public float Width => Right - X;
    }

    private readonly record struct Band(float YTop, float YBottom, IReadOnlyList<HInterval> Segments);

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

    private static float NextBoundary(IReadOnlyList<float> boundaries, float y)
    {
        foreach (var b in boundaries)
        {
            if (b > y + Epsilon)
            {
                return b;
            }
        }
        return y + 1f;
    }
}
