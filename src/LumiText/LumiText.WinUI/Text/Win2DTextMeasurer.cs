using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.WinUI.Text;

/// <summary>
/// <see cref="ITextMeasurer"/> 的 Win2D/DirectWrite 实现。
/// 首行探测走 <see cref="CanvasTextLayout.LineMetrics"/>：DirectWrite 建立布局时已完成断行，
/// 行度量一次读回（每行字符数 / 行高 / 基线），不需要遍历字形。
/// </summary>
/// <remarks>
/// 旧实现用 <c>DrawToTextRenderer</c> 探基线（把整段每个字形 run 经托管回调封送一遍）
/// 再用 <c>GetCharacterRegions</c> 求范围，单次首行探测 8.2ms（1 万字符整段 201ms），
/// 直接导致一次全量排版 2028ms。换 LineMetrics 后同一探测降到 0.3ms 量级，
/// 依据见 Demo 的性能验收 [A]/[B] 与度量拆解报告。
/// </remarks>
public sealed class Win2DTextMeasurer : ITextMeasurer
{
    /// <summary>布局高度上限：限制一次布局计算的行数（换取更小的单次延迟）。</summary>
    private const float LayoutHeight = 4096f;

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

        var layout = new CanvasTextLayout(_device, text, GetFormat(style), maxWidth, LayoutHeight);
        try
        {
            var lines = layout.LineMetrics;
            if (lines.Length == 0 || lines[0].CharacterCount <= 0)
            {
                layout.Dispose();
                return default;
            }

            var first = lines[0];
            int consumed = Math.Min(first.CharacterCount, text.Length);
            // 行推进宽度：末字符之后的插入符 X（比逐字符区域并集便宜三个数量级）。
            float width = layout.GetCaretPosition(consumed, false).X;
            return new FirstLineInfo(
                consumed, width, first.Baseline, first.Height - first.Baseline, layout);
        }
        catch
        {
            layout.Dispose();
            throw;
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
}
