using System.Diagnostics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace LumiText.Demo;

/// <summary>
/// M1-U2 探针：验证「行内样式 Set* 必须在读 LineMetrics 之前应用」的顺序约束（Phase 1 设计 §5.3）。
/// 量化三件事：① Set* 之后必须重读 LineMetrics 才能拿到带样式的度量（不重读 = 用错数据）；
/// ② 「先读后设再重读」比「先设后读」多花的成本（白排一次的量级）；
/// ③ Set* 本身的调用开销（应为微秒级，成本全在它触发的重排上）。
/// </summary>
internal static class StyleOrderProbe
{
    private const int Samples = 10;
    private const int Warmup = 3;
    private const float LayoutWidth = 520f;   // 与 S2 度量口径一致
    private const float LayoutHeight = 4096f;

    public static string Run()
    {
        var device = CanvasDevice.GetSharedDevice();
        string text = BuildText(400);
        var report = new StringBuilder();
        report.AppendLine($"环境：{Environment.OSVersion.VersionString} / {DateTime.Now:yyyy-MM-dd HH:mm} / " +
                          $"{(IsDebugBuild() ? "Debug" : "Release")}");
        report.AppendLine($"文本 {text.Length} 字符，宽 {LayoutWidth} dip；热身 {Warmup} + {Samples} 次采样（ms）");

        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 15f,
            WordWrapping = CanvasWordWrapping.Wrap,
        };

        // ---- ① 正确性：样式影响排版；先读后设不重读 = 拿到的是无样式度量 ----
        int plainLines, styledLines;
        float plainHeight, styledHeight;
        using (var layout = new CanvasTextLayout(device, text, format, LayoutWidth, LayoutHeight))
        {
            var m = layout.LineMetrics;
            plainLines = m.Length;
            plainHeight = m.Sum(x => x.Height);
        }
        using (var layout = new CanvasTextLayout(device, text, format, LayoutWidth, LayoutHeight))
        {
            ApplyStyles(layout);
            var m = layout.LineMetrics;
            styledLines = m.Length;
            styledHeight = m.Sum(x => x.Height);
        }
        int staleLines;
        using (var layout = new CanvasTextLayout(device, text, format, LayoutWidth, LayoutHeight))
        {
            // 错误顺序：先读（缓存了无样式度量）→ 再设样式 → 不重读就直接用旧值
            staleLines = layout.LineMetrics.Length;
            ApplyStyles(layout);
        }
        report.AppendLine();
        report.AppendLine($"无样式：{plainLines} 行，累计行高 {plainHeight:F1} dip");
        report.AppendLine($"带样式（先设后读）：{styledLines} 行，累计行高 {styledHeight:F1} dip");
        report.AppendLine($"先读后设（不重读，即为旧度量）：{staleLines} 行 ← 与无样式一致 = 用错数据的实证");
        report.AppendLine(staleLines == plainLines && styledLines != plainLines
            ? "→ 结论 A 成立：Set* 使既有 LineMetrics 失效，不重读拿到的是无样式旧度量；样式确实改变断行。"
            : "→ 注意：行数未区分（样式未改变断行），以耗时对比为准。");

        // ---- ② 顺序 A（先设后读）vs 顺序 B（先读后设再重读）----
        var aTotal = Sample(() =>
        {
            using var layout = new CanvasTextLayout(device, text, format, LayoutWidth, LayoutHeight);
            ApplyStyles(layout);
            _ = layout.LineMetrics;
        });

        var bCreate = new double[Samples];
        var bFirstRead = new double[Samples];
        var bApply = new double[Samples];
        var bReRead = new double[Samples];
        for (int i = 0; i < Warmup + Samples; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var layout = new CanvasTextLayout(device, text, format, LayoutWidth, LayoutHeight);
            long t1 = Stopwatch.GetTimestamp();
            _ = layout.LineMetrics;
            long t2 = Stopwatch.GetTimestamp();
            ApplyStyles(layout);
            long t3 = Stopwatch.GetTimestamp();
            _ = layout.LineMetrics;
            long t4 = Stopwatch.GetTimestamp();
            layout.Dispose();
            if (i >= Warmup)
            {
                int k = i - Warmup;
                bCreate[k] = Ms(t0, t1);
                bFirstRead[k] = Ms(t1, t2);
                bApply[k] = Ms(t2, t3);
                bReRead[k] = Ms(t3, t4);
            }
        }

        report.AppendLine();
        report.AppendLine($"顺序 A 建布局+设样式+读度量　{Stats(aTotal)}");
        report.AppendLine($"顺序 B 分解：建布局 {Stats(bCreate)}");
        report.AppendLine($"　　　　　 首次读度量 {Stats(bFirstRead)}");
        report.AppendLine($"　　　　　 Set* ×6 {Stats(bApply)}（微秒级，成本不在调用本身）");
        report.AppendLine($"　　　　　 设后重读度量 {Stats(bReRead)}");
        double aMedian = Median(aTotal);
        double bMedian = Median(bCreate) + Median(bFirstRead) + Median(bApply) + Median(bReRead);
        report.AppendLine($"顺序 B 合计中位 {bMedian:F4} ms vs 顺序 A 中位 {aMedian:F4} ms → " +
                          $"白排一次 ≈ {bMedian - aMedian:F4} ms（≈ 一次排版成本，即「先读后设等于白排一次」的量化）");

        return report.ToString();
    }

    /// <summary>六种行内样式各应用一段（Phase 1 §5.3 列出的全部 API）。</summary>
    private static void ApplyStyles(CanvasTextLayout layout)
    {
        layout.SetFontWeight(10, 20, Microsoft.UI.Text.FontWeights.Bold);
        layout.SetFontStyle(50, 30, Windows.UI.Text.FontStyle.Italic);
        layout.SetStrikethrough(100, 20, true);
        layout.SetUnderline(150, 20, true);
        layout.SetColor(200, 20, Color.FromArgb(255, 180, 40, 60));
        layout.SetFontSize(250, 20, 22f);
    }

    private static double[] Sample(Action action)
    {
        for (int i = 0; i < Warmup; i++)
        {
            action();
        }
        var samples = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            samples[i] = Ms(t0, Stopwatch.GetTimestamp());
        }
        return samples;
    }

    private static double Ms(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;

    private static string Stats(double[] samples)
    {
        var sorted = samples.OrderBy(v => v).ToArray();
        return $"中位 {Median(sorted):F4}　P95 {sorted[(int)((sorted.Length - 1) * 0.95)]:F4}　最大 {sorted[^1]:F4}";
    }

    private static double Median(double[] samples)
    {
        var sorted = samples.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static string BuildText(int chars)
    {
        const string sentence = "排版引擎把可用宽度按浮动矩形切成若干段，The quick brown fox jumps over the lazy dog. 文字按段填充。";
        var buffer = new StringBuilder(chars + sentence.Length);
        while (buffer.Length < chars)
        {
            buffer.Append(sentence);
        }
        return buffer.ToString(0, chars);
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
