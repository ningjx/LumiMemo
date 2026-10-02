using System.Diagnostics;
using System.Text;
using LumiText.Core.Documents;
using LumiText.Core.Layout;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;

namespace LumiText.Demo;

/// <summary>
/// S2 验收的排版性能测量（设计文档 phase0-spike-design.md §3.6）。
/// 只测排版通路（引擎 + DirectWrite 度量 + 旧结果释放），不含绘制；
/// 拖动期帧间隔由 <see cref="DemoWindow"/> 的帧率记录板单独采集。
/// </summary>
internal static class PerfBenchmark
{
    private const float BenchWidth = 520f;   // 典型便签内容宽（dip）
    private const int FullDocChars = 10_000;
    private const int DragDocChars = 3_000;
    private const int DragSteps = 60;
    private const int WarmupRuns = 3;
    private const int Samples = 10;

    /// <summary>跑 [A] 全量排版与 [B] 拖动逐帧重排，返回完整报告与一行摘要。</summary>
    public static (string Report, string Summary) RunLayoutBenchmarks()
    {
        var renderer = new FlowDocumentRenderer(new Win2DTextMeasurer());
        var report = new StringBuilder();
        report.AppendLine($"环境：{Environment.OSVersion.VersionString} / {DateTime.Now:yyyy-MM-dd HH:mm} / " +
                          $"{(IsDebugBuild() ? "Debug" : "Release")}");
        report.AppendLine($"内容宽 {BenchWidth} dip；热身 {WarmupRuns} 次，取 {Samples} 次采样");

        // [A] 全量排版：10,000 字符 + 3 个浮动，预算 < 8ms
        var fullDoc = BuildParagraphs(FullDocChars);
        var floats = new[]
        {
            new FloatObject(1, new LayoutRect(16, 120, 150, 110), FloatSide.Left, 8f),
            new FloatObject(2, new LayoutRect(354, 1200, 150, 120), FloatSide.Right, 8f),
            new FloatObject(3, new LayoutRect(40, 2600, 150, 100), FloatSide.Left, 8f),
        };
        var engineMs = new double[Samples];
        var wallMs = new double[Samples];
        for (int i = 0; i < WarmupRuns; i++)
        {
            renderer.UpdateLayout(fullDoc, floats, BenchWidth);
        }
        for (int i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            renderer.UpdateLayout(fullDoc, floats, BenchWidth);
            wallMs[i] = watch.Elapsed.TotalMilliseconds;
            engineMs[i] = renderer.LastLayoutDuration.TotalMilliseconds;
        }
        report.AppendLine();
        report.AppendLine($"[A] 全量排版 {FullDocChars:N0} 字符 + 3 浮动　预算 < 8.00 ms");
        report.AppendLine($"    引擎排版　　{Stats(engineMs)}");
        report.AppendLine($"    含释放旧布局 {Stats(wallMs)}");

        // [B] 拖动逐帧重排：3,000 字符 + 1 浮动走完整个可用宽度，预算 < 4ms/帧
        var dragDoc = BuildParagraphs(DragDocChars);
        var dragFloat = new FloatObject(1, new LayoutRect(0, 80, 150, 110), FloatSide.Left, 8f);
        float maxX = BenchWidth - dragFloat.Rect.Width;
        var frames = new double[DragSteps];
        for (int i = 0; i < DragSteps; i++)
        {
            float x = maxX * i / (DragSteps - 1);
            var watch = Stopwatch.StartNew();
            renderer.UpdateLayout(dragDoc, new[] { dragFloat.MovedTo(x, 80) }, BenchWidth);
            frames[i] = watch.Elapsed.TotalMilliseconds;
        }
        report.AppendLine();
        report.AppendLine($"[B] 拖动逐帧重排 {DragDocChars:N0} 字符 + 1 浮动（{DragSteps} 步全量重排）　预算 < 4.00 ms/帧");
        report.AppendLine($"    每帧墙钟　　{Stats(frames)}");
        report.AppendLine("    说明：当前每次拖动都是全量重排（设计文档 §3.2 的增量重排未实施），" +
                          "本行即「每帧完整重排」成本。");

        string summary =
            $"[A] 1万字符引擎中位 {Percentile(engineMs.OrderBy(v => v).ToArray(), 0.5):F2} ms｜" +
            $"[B] 每帧 P95 {Percentile(frames.OrderBy(v => v).ToArray(), 0.95):F2} ms　→ 原始数据见 s2-perf.txt";
        return (report.ToString(), summary);
    }

    /// <summary>帧间隔统计（毫秒样本）→ 平均 fps / P95 / 最长帧。</summary>
    public static string FrameStats(IReadOnlyList<double> frameMs)
    {
        if (frameMs.Count < 2)
        {
            return "样本不足，未记录。";
        }
        var sorted = frameMs.OrderBy(v => v).ToArray();
        double mean = sorted.Average();
        double p95 = Percentile(sorted, 0.95);
        double max = sorted[^1];
        double over18 = sorted.Count(v => v > 18.0) * 100.0 / sorted.Length;
        return $"帧数 {sorted.Length}，时长 {sorted.Sum() / 1000.0:F1} s；" +
               $"平均 {1000.0 / mean:F1} fps，P95 帧长 {p95:F2} ms，最长 {max:F2} ms；" +
               $"超 18 ms 的帧占 {over18:F1}%";
    }

    /// <summary>把一段报告追加进 exe 同目录的 s2-perf.txt（便于外部读取原始数据）。</summary>
    public static string Append(string section, string body)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "s2-perf.txt");
        try
        {
            File.AppendAllText(path, $"{Environment.NewLine}===== {section} ====={Environment.NewLine}{body}");
            return path;
        }
        catch (Exception ex)
        {
            return $"（写 {path} 失败：{ex.Message}）";
        }
    }

    private static string Stats(double[] samples)
    {
        var sorted = samples.OrderBy(v => v).ToArray();
        return $"中位 {Percentile(sorted, 0.5):F2} ms　P95 {Percentile(sorted, 0.95):F2} ms　" +
               $"最小 {sorted[0]:F2} ms　最大 {sorted[^1]:F2} ms";
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    /// <summary>造目标长度的中英混排文档，按 ~400 字符切段。</summary>
    private static ParagraphBlock[] BuildParagraphs(int totalChars)
    {
        string[] sentences =
        {
            "排版引擎在纯计算空间工作，把每行的可用宽度按浮动矩形切成若干段，文字按段填充，",
            "The quick brown fox jumps over the lazy dog. ",
            "行高跨越浮动边缘时取所跨各带段集合的交集，矮图片只挤占相交的那几行。",
            "1234567890 混排 English 与数字同样正确断行。",
            "同一带内若有多个段（图片两侧），行盒共享基线，文字在图片两侧对齐如常。",
        };
        const int paragraphChars = 400;
        var paragraphs = new List<ParagraphBlock>(totalChars / paragraphChars + 1);
        var buffer = new StringBuilder(paragraphChars + 64);
        int sentence = 0;
        while (paragraphs.Count * paragraphChars < totalChars)
        {
            buffer.Clear();
            while (buffer.Length < paragraphChars)
            {
                buffer.Append(sentences[sentence++ % sentences.Length]);
            }
            paragraphs.Add(new ParagraphBlock(buffer.ToString(0, paragraphChars), SpaceAfter: 10f));
        }
        return paragraphs.ToArray();
    }
}
