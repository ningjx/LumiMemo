using System.IO;
using LumiMemo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LumiMemo.WinUI.Services;

/// <summary>按便签去抖落盘：连续编辑只触发一次保存。</summary>
/// <remarks>
/// <para>
/// 骨架照抄 WPF 版（已随项目删除），只差最后一环：这里没有可按 id 反查的 NoteStore，
/// RTF 在窗口的编辑区里——「保存这张便签」只能是调用方给的闭包，因此
/// <see cref="ScheduleSave"/> 收一个 <c>Func&lt;Task&gt;</c>；连续调用是
/// 「重新计时 + 换最新委托」。
/// </para>
/// <para>
/// 定时器走 <see cref="IUiTimerFactory"/> 端口，理由与原版相同：
/// 「连打五个字符只写一次盘」必须能在无头测试里确定性地验。
/// </para>
/// </remarks>
public sealed class AutoSaveService(IUiTimerFactory timers, ILogger<AutoSaveService> logger)
    : IDisposable
{
    private readonly Dictionary<Guid, Entry> _pending = [];
    private bool _disposed;

    /// <summary>去抖时长。组合根从 <c>settings.autoSaveDelayMs</c> 灌入。</summary>
    public int DelayMilliseconds { get; set; } = 500;

    /// <summary>当前有几张便签在等着落盘。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>安排一次落盘。连续调用是<strong>重新计时</strong>，不是排队。</summary>
    public void ScheduleSave(Guid noteId, Func<Task> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_pending.TryGetValue(noteId, out Entry? entry))
        {
            entry = new Entry(timers.Create());
            _pending[noteId] = entry;
        }

        entry.Persist = persist;
        entry.Timer.Start(TimeSpan.FromMilliseconds(DelayMilliseconds), () => OnTimerDue(noteId));
    }

    /// <summary>
    /// 取消等待中的那一轮，并移除条目。
    /// </summary>
    /// <remarks>
    /// 移除条目而不是只停表：委托闭包持着 ViewModel 与编辑区，窗口关闭后它们都该被回收。
    /// </remarks>
    public void CancelScheduledSave(Guid noteId)
    {
        if (_pending.Remove(noteId, out Entry? entry))
        {
            entry.Timer.Stop();
            entry.Timer.Dispose();
        }
    }

    /// <summary>立刻保存，绕过去抖。关单张便签的窗口时用。</summary>
    public async Task SaveNowAsync(Guid noteId)
    {
        if (_pending.TryGetValue(noteId, out Entry? entry))
        {
            CancelScheduledSave(noteId);
            await RunAsync(entry);
        }
    }

    /// <summary>把还在等待的全部立刻落盘。退出流程用。</summary>
    /// <remarks>
    /// 先取一份 id 快照再遍历：执行期间若有回调又调了 <see cref="ScheduleSave"/>，
    /// 直接遍历字典会抛 <c>Collection was modified</c>。
    /// </remarks>
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        Guid[] pending = [.. _pending.Keys];

        foreach (Guid noteId in pending)
        {
            ct.ThrowIfCancellationRequested();
            await SaveNowAsync(noteId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Entry entry in _pending.Values)
        {
            entry.Timer.Dispose();
        }

        _pending.Clear();
    }

    private void OnTimerDue(Guid noteId)
    {
        if (_pending.TryGetValue(noteId, out Entry? entry))
        {
            CancelScheduledSave(noteId);
            _ = RunAsync(entry);
        }
    }

    /// <summary>跑一次保存。异常在 ViewModel 的 PersistAsync 里已被处理成状态文案，这里只兜底。</summary>
    private async Task RunAsync(Entry entry)
    {
        try
        {
            await entry.Persist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 防「未观察的 Task 异常」：不抛出去，至少留下一条日志。
            logger.LogWarning("自动保存失败：{ExceptionType}。", ex.GetType().Name);
        }
    }

    private sealed class Entry(IUiTimer timer)
    {
        public IUiTimer Timer { get; } = timer;

        public Func<Task> Persist { get; set; } = static () => Task.CompletedTask;
    }
}
