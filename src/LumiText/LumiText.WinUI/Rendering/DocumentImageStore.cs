using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using LumiText.Core.Documents;

namespace LumiText.WinUI.Rendering;

/// <summary>
/// 文档图片的解码缓存（Phase 1 设计 §8.1）：把 <see cref="ImageResource"/> 的 base64/字节
/// 解码为 <see cref="CanvasBitmap"/> 字典（ImageId → bitmap），由宿主（渲染器）持有，
/// 文档释放时统一 <see cref="Dispose"/>。
/// </summary>
/// <remarks>
/// <para><b>排版不依赖解码结果</b>（显示尺寸在 <see cref="ImageBlock"/> 模型里），解码与排版并行：
/// 文档载入即排版上屏，位图就绪后经 <c>onImageReady</c> 回调（宿主 Invalidate）补画；
/// 解码失败的图片记为失败态，渲染层画占位框，不阻塞正文。</para>
/// <para>解码放后台线程（<see cref="Task.Run"/>，并行上限 4）：CanvasBitmap 与设备关联，
/// 绘制回 UI/合成线程安全——Win2D 资源创建本就支持非 UI 线程（§8.1，以异常为信号验证过）。</para>
/// </remarks>
public sealed class DocumentImageStore : IDisposable
{
    /// <summary>解码并行上限（§10.3 预热策略）。</summary>
    public const int MaxParallel = 4;

    private readonly CanvasDevice _device;
    private readonly ConcurrentDictionary<string, CanvasBitmap> _bitmaps = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly List<(string Id, int Bytes, double Ms)> _decodeLog = new();
    private readonly object _logLock = new();

    public DocumentImageStore(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>已就绪的位图数。</summary>
    public int LoadedCount => _bitmaps.Count;

    /// <summary>
    /// 后台并行预热解码（解码完成一张回调一张 <paramref name="onImageReady"/>，
    /// 回调在后台线程触发，宿主负责封送回 UI 线程）。幂等：已就绪/已失败的不再解码。
    /// </summary>
    public async Task WarmupAsync(
        IReadOnlyList<ImageResource>? images,
        Action? onImageReady = null,
        CancellationToken cancellationToken = default)
    {
        if (images is null or { Count: 0 })
        {
            return;
        }

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = new List<Task>(images.Count);
        foreach (var image in images)
        {
            if (_bitmaps.ContainsKey(image.Id) || _failed.ContainsKey(image.Id))
            {
                continue;
            }
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var watch = Stopwatch.StartNew();
                    var bitmap = await DecodeAsync(image.Data, cancellationToken).ConfigureAwait(false);
                    watch.Stop();
                    _bitmaps[image.Id] = bitmap;
                    lock (_logLock)
                    {
                        _decodeLog.Add((image.Id, image.Data.Length, watch.Elapsed.TotalMilliseconds));
                    }
                    onImageReady?.Invoke();
                }
                catch (Exception)
                {
                    // 解码失败：渲染层画占位框并可在日志侧查 FailureCount（§8.1，不阻塞正文）。
                    _failed[image.Id] = 0;
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>取已解码位图；未就绪/失败返回 <see langword="null"/>（渲染层画占位）。</summary>
    public CanvasBitmap? TryGet(string imageId) =>
        _bitmaps.TryGetValue(imageId, out var bitmap) ? bitmap : null;

    /// <summary>解码是否已失败（失败也画占位，但可在报告/日志中区分）。</summary>
    public bool IsFailed(string imageId) => _failed.ContainsKey(imageId);

    /// <summary>解码耗时台账（Id, 字节数, 毫秒），供 §10.3 基准写入验收记录。</summary>
    public IReadOnlyList<(string Id, int Bytes, double Ms)> DecodeLog
    {
        get { lock (_logLock) { return _decodeLog.ToArray(); } }
    }

    private async Task<CanvasBitmap> DecodeAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        return await CanvasBitmap.LoadAsync(_device, stream).AsTask(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap.Dispose();
        }
        _bitmaps.Clear();
    }
}
