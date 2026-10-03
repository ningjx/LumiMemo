using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.WinUI.Text;

/// <summary>
/// <see cref="ITextMeasurer"/> 的 Win2D/DirectWrite 实现（M3 批量接口版）。
/// 一个段（X，宽）只建一次 <see cref="CanvasTextLayout"/>、一次读回该宽度下的全部行
/// （<see cref="CanvasTextLayout.LineMetrics"/>，1 万字符实测 0.011ms），
/// 消除旧首行探测逐行重建布局的 O(字符数²/行高) 成本。
/// </summary>
/// <remarks>
/// 行内样式纪律（M1-U2 实证）：必须在读 <see cref="CanvasTextLayout.LineMetrics"/> 之前
/// 完成全部 Set*——Set* 使既有行度量失效，先读后设 = 拿到无样式旧度量（正确性问题）
/// 且多付一次无样式排版（≈0.28ms/400 字）。另：CanvasTextLayout 构造是懒惰的，
/// 真实排版发生在首次读度量时；带样式排版成本约为无样式的 4–5 倍（M3 基准须含多样式场景）。
/// </remarks>
public sealed class Win2DTextMeasurer : ITextMeasurer
{
    /// <summary>布局高度上限：限制一次布局计算的行数（批耗尽后引擎自然以剩余文本建新批，正确性不受影响）。</summary>
    private const float LayoutHeight = 4096f;

    private readonly CanvasDevice _device;
    private readonly Dictionary<TextStyle, CanvasTextFormat> _formats = new();

    public Win2DTextMeasurer()
    {
        // 共享设备：多窗口/多编辑器只持有一份 D2D 设备（设备丢失重建入口属 R7，Phase 2 实装）。
        _device = CanvasDevice.GetSharedDevice();
    }

    public ILineBatch LayoutLines(IReadOnlyList<TextRun> runs, TextStyle baseStyle, float maxWidth)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0 || maxWidth < 1f)
        {
            return Win2DLineBatch.Empty;
        }

        string fullText = string.Concat(runs.Select(static r => r.Text));
        if (fullText.Length == 0)
        {
            return Win2DLineBatch.Empty;
        }

        var layout = new CanvasTextLayout(_device, fullText, GetFormat(baseStyle), maxWidth, LayoutHeight);
        try
        {
            // U2 纪律：先设样式，后读行度量。
            ApplyInlineStyles(layout, runs, baseStyle);

            var metrics = layout.LineMetrics;
            if (metrics.Length == 0 || metrics[0].CharacterCount <= 0)
            {
                layout.Dispose();
                return Win2DLineBatch.Empty;
            }

            var lines = new MeasuredLine[metrics.Length];
            int charStart = 0;
            float offsetY = 0f;
            for (int i = 0; i < metrics.Length; i++)
            {
                var m = metrics[i];
                int consumed = Math.Min(m.CharacterCount, fullText.Length - charStart);
                // 行推进宽度：末字符之后的插入符 X（比逐字符区域并集便宜三个数量级）。
                float width = layout.GetCaretPosition(charStart + consumed, false).X;
                lines[i] = new MeasuredLine(
                    charStart, consumed, width, m.Baseline, m.Height - m.Baseline, offsetY);
                offsetY += m.Height;
                charStart += consumed;
            }
            return new Win2DLineBatch(layout, lines);
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    /// <summary>逐 run 应用行内样式（Win2D API 已查证，V5；调用时机见类注释的 U2 纪律）。</summary>
    private static void ApplyInlineStyles(
        CanvasTextLayout layout, IReadOnlyList<TextRun> runs, TextStyle baseStyle)
    {
        int start = 0;
        foreach (var run in runs)
        {
            int count = run.Text.Length;
            if (run.Style is { } style && count > 0)
            {
                if (style.Bold)
                {
                    layout.SetFontWeight(start, count, Microsoft.UI.Text.FontWeights.Bold);
                }
                if (style.Italic)
                {
                    layout.SetFontStyle(start, count, Windows.UI.Text.FontStyle.Italic);
                }
                if (style.Strikethrough)
                {
                    layout.SetStrikethrough(start, count, true);
                }
                if (style.Underline)
                {
                    layout.SetUnderline(start, count, true);
                }
                if (style.Color is { } color)
                {
                    layout.SetColor(start, count,
                        Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));
                }
                if (style.FontSizeRatio is { } ratio)
                {
                    layout.SetFontSize(start, count, baseStyle.FontSize * ratio);
                }
            }
            start += count;
        }
    }

    public LineHeightInfo MeasureLineHeight(TextStyle style)
    {
        using var layout = new CanvasTextLayout(_device, "Mg", GetFormat(style), 4096f, LayoutHeight);
        var lines = layout.LineMetrics;
        return lines.Length == 0
            ? default
            : new LineHeightInfo(lines[0].Baseline, lines[0].Height - lines[0].Baseline);
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

    /// <summary>一批行的 Win2D 实现：持有整批共享的 <see cref="CanvasTextLayout"/> 与逐行度量。</summary>
    private sealed class Win2DLineBatch : ILineBatch
    {
        public static readonly Win2DLineBatch Empty = new(null, Array.Empty<MeasuredLine>());

        private readonly CanvasTextLayout? _layout;
        private readonly MeasuredLine[] _lines;

        public Win2DLineBatch(CanvasTextLayout? layout, MeasuredLine[] lines)
        {
            _layout = layout;
            _lines = lines;
        }

        public int LineCount => _lines.Length;

        public MeasuredLine GetLine(int index) => _lines[index];

        public object? NativeLayout => _layout;

        public CharHit? HitTestChar(float x, float y)
        {
            if (_layout is null)
            {
                return null;
            }
            bool hit = _layout.HitTest(x, y, out var region, out bool isTrailing);
            return hit ? new CharHit(region.CharacterIndex, isTrailing) : null;
        }

        public (float X, float YTop, float Height) GetCaretGeometry(int characterIndex, bool isTrailing)
        {
            if (_layout is null)
            {
                return (0f, 0f, 0f);
            }
            int clamped = Math.Clamp(characterIndex, 0, GetTextLength());
            var point = _layout.GetCaretPosition(clamped, isTrailing);
            // 光标高度随行高：clamped == lineEnd 时优先下一行（isTrailing=false 的段首语义）
            for (int i = 0; i < _lines.Length; i++)
            {
                var line = _lines[i];
                int lineEnd = line.CharStart + line.CharsConsumed;
                bool isLast = i == _lines.Length - 1;
                if (clamped < lineEnd || (isLast && clamped <= lineEnd))
                {
                    return (point.X, line.OffsetY, line.Ascent + line.Descent);
                }
            }
            var last = _lines[^1];
            return (point.X, last.OffsetY, last.Ascent + last.Descent);
        }

        public IReadOnlyList<CharRegion> GetCharRegions(int characterIndex, int characterCount)
        {
            if (_layout is null || characterCount <= 0)
            {
                return Array.Empty<CharRegion>();
            }
            int textLength = GetTextLength();
            int clampedIndex = Math.Clamp(characterIndex, 0, textLength);
            int clampedCount = Math.Clamp(characterCount, 0, textLength - clampedIndex);
            if (clampedCount <= 0)
            {
                return Array.Empty<CharRegion>();
            }
            var regions = _layout.GetCharacterRegions(clampedIndex, clampedCount);
            var result = new CharRegion[regions.Length];
            for (int i = 0; i < regions.Length; i++)
            {
                var r = regions[i];
                result[i] = new CharRegion(
                    r.CharacterIndex, r.CharacterCount,
                    (float)r.LayoutBounds.X, (float)r.LayoutBounds.Y,
                    (float)r.LayoutBounds.Width, (float)r.LayoutBounds.Height);
            }
            return result;
        }

        private int GetTextLength()
        {
            if (_lines.Length == 0)
            {
                return 0;
            }
            var last = _lines[^1];
            return last.CharStart + last.CharsConsumed;
        }

        public void Dispose() => _layout?.Dispose();
    }
}
