using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.WinUI.Text;

/// <summary>
/// <see cref="ITextMeasurer"/> 的 Win2D/DirectWrite 实现。
/// 以 <see cref="CanvasTextLayout"/> 做首行探测；随行的布局对象作为
/// <see cref="FirstLineInfo.NativeLayout"/> 透传给渲染层直接绘制（避免二次排版）。
/// </summary>
public sealed class Win2DTextMeasurer : ITextMeasurer
{
    private readonly CanvasDevice _device;
    private readonly Dictionary<TextStyle, CanvasTextFormat> _formats = new();

    public Win2DTextMeasurer()
    {
        // 共享设备：多窗口/多编辑器只持有一份 D2D 设备（设备丢失重建由渲染层统一处理）。
        _device = CanvasDevice.GetSharedDevice();
    }

    public FirstLineInfo LayoutFirstLine(string text, TextStyle style, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 1f)
        {
            return default;
        }

        var layout = new CanvasTextLayout(_device, text, GetFormat(style), maxWidth, 4096f);
        try
        {
            var firstCharRegion = UnionRegions(layout, 1);
            int consumed = CountFirstLineChars(layout, text.Length, firstCharRegion.Height);
            if (consumed <= 0)
            {
                layout.Dispose();
                return default;
            }

            var region = UnionRegions(layout, consumed);
            float baseline = ProbeBaseline(layout) ?? region.Height * 0.8f;
            float ascent = baseline - region.Y;
            float descent = Math.Max(0f, region.Bottom - baseline);
            return new FirstLineInfo(consumed, region.Width, ascent, descent, layout);
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    public LineHeightInfo MeasureLineHeight(TextStyle style)
    {
        using var layout = new CanvasTextLayout(_device, "Mg", GetFormat(style), 4096f, 4096f);
        var region = UnionRegions(layout, 2);
        float baseline = ProbeBaseline(layout) ?? region.Height * 0.8f;
        return new LineHeightInfo(baseline - region.Y, Math.Max(0f, region.Bottom - baseline));
    }

    private CanvasTextFormat GetFormat(TextStyle style)
    {
        if (!_formats.TryGetValue(style, out var format))
        {
            format = new CanvasTextFormat
            {
                FontFamily = style.FontFamily,
                FontSize = style.FontSize,
                WordWrapping = CanvasWordWrapping.Wrap,
            };
            _formats[style] = format;
        }
        return format;
    }

    /// <summary>
    /// 二分查找第一行容纳的字符数（插入符 Y 随文本位置单调不减）。
    /// </summary>
    /// <remarks>
    /// 阈值必须是"半行高"而不是接近 0 的小量：字体回退时，同行内回退字体（如 CJK）
    /// 与主字体（Latin）的插入符 Y 会有亚像素级差异（实测 0.95dip/行高 20），
    /// 用小阈值会把第一个回退字符误判为换行。见 spikes/Probe 的探针输出。
    /// </remarks>
    private static int CountFirstLineChars(CanvasTextLayout layout, int textLength, float lineHeight)
    {
        float threshold = Math.Max(1f, lineHeight * 0.5f);
        int lo = 1, hi = textLength, result = textLength;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            float y = layout.GetCaretPosition(mid, false).Y;
            if (y > threshold)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    /// <summary>前 <paramref name="count"/> 个字符的布局区域并集（含行盒高度）。</summary>
    private static RectF UnionRegions(CanvasTextLayout layout, int count)
    {
        var regions = layout.GetCharacterRegions(0, count);
        if (regions.Length == 0)
        {
            return new RectF(0, 0, 0, 0);
        }
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var r in regions)
        {
            var b = r.LayoutBounds;
            left = Math.Min(left, (float)b.X);
            top = Math.Min(top, (float)b.Y);
            right = Math.Max(right, (float)b.Right);
            bottom = Math.Max(bottom, (float)b.Bottom);
        }
        return new RectF(left, top, right - left, bottom - top);
    }

    /// <summary>经自定义文本渲染器探取第一行基线 Y（DrawGlyphRun 的点即基线原点）。</summary>
    private static float? ProbeBaseline(CanvasTextLayout layout)
    {
        var probe = new BaselineProbe();
        layout.DrawToTextRenderer(probe, 0, 0);
        return probe.Baseline;
    }

    private readonly record struct RectF(float X, float Y, float Width, float Height)
    {
        public float Bottom => Y + Height;
    }

    /// <summary>只取首个字形run基线的探针渲染器；其余回调全部空实现。</summary>
    private sealed class BaselineProbe : ICanvasTextRenderer
    {
        public float? Baseline { get; private set; }

        public float Dpi => 96f;

        public bool PixelSnappingDisabled => false;

        public System.Numerics.Matrix3x2 Transform => System.Numerics.Matrix3x2.Identity;

        public void DrawGlyphRun(
            System.Numerics.Vector2 point,
            CanvasFontFace fontFace,
            float fontSize,
            CanvasGlyph[] glyphs,
            bool isSideways,
            uint bidiLevel,
            object brush,
            CanvasTextMeasuringMode measuringMode,
            string localeName,
            string textString,
            int[] clusterMapIndices,
            uint textPosition,
            CanvasGlyphOrientation glyphOrientation)
        {
            Baseline ??= point.Y;
        }

        public void DrawStrikethrough(
            System.Numerics.Vector2 point, float strikethroughWidth, float strikethroughThickness,
            float strikethroughOffset, CanvasTextDirection textDirection, object brush,
            CanvasTextMeasuringMode measuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation)
        {
        }

        public void DrawUnderline(
            System.Numerics.Vector2 point, float underlineWidth, float underlineThickness,
            float underlineOffset, float underlineHeight, CanvasTextDirection textDirection,
            object brush, CanvasTextMeasuringMode measuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation)
        {
        }

        public void DrawInlineObject(
            System.Numerics.Vector2 point, ICanvasTextInlineObject inlineObject,
            bool isSideways, bool isRightToLeft, object brush,
            CanvasGlyphOrientation glyphOrientation)
        {
        }
    }
}
