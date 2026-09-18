using System.IO;
using LumiMemo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// 按便签去抖落盘（§11.1、§11.3）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>LayoutService</c> 的节流是同一套思路，区别在于<strong>粒度</strong>：
/// <c>layout.json</c> 是一个整文件，所以那边只有一个定时器；便签是一张一个文件，
/// 同时编辑两张就必须各等各的 500 毫秒，因此这里按 id 各持一个定时器。
/// </para>
/// <para>
/// 定时器走的是 <see cref="IUiTimerFactory"/> 端口而不是直接 <c>DispatcherTimer</c>：
/// 「连打 5 个字符只写一次盘」这条断言必须能在无头测试里确定性地验，
/// 而真实 <c>DispatcherTimer</c> 在没有消息泵的测试进程里永远不会到期。
/// </para>
/// <para>
/// <strong>保存失败只记日志，不打断用户</strong>（§11.5 的简化处理）：
/// 这是一个后台去抖动作，此刻用户可能正在另一个窗口里打字，
/// 弹一个模态对话框出来会把输入焦点抢走。等有了状态条，失败会显示在那上面。
/// </para>
/// </remarks>
public sealed class AutoSaveService : IDisposable
{
    private readonly INoteService _noteService;
    private readonly IUiTimerFactory _timers;
    private readonly ILogger<AutoSaveService> _logger;

    private readonly Dictionary<Guid, IUiTimer> _pending = [];

    private bool _isDisposed;

    public AutoSaveService(
        INoteService noteService,
        IUiTimerFactory timers,
        ILogger<AutoSaveService> logger)
    {
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(logger);

        _noteService = noteService;
        _timers = timers;
        _logger = logger;
    }

    /// <summary>
    /// 去抖时长。由启动序列从 <c>settings.autoSaveDelayMs</c> 灌进来（§17.1）。
    /// </summary>
    /// <remarks>
    /// 范围 300–800 已由 <c>JsonSettingsStore</c> 在读取时钳制过，这里不再校验：
    /// 设置是本程序自己写的，重复校验只会让「谁负责」这件事变模糊。
    /// </remarks>
    public int DelayMilliseconds { get; set; } = 500;

    /// <summary>当前有几张便签在等着落盘。退出流程需要据此判断要不要提示。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 安排一次落盘。连续调用是<strong>重新计时</strong>，不是排队——
    /// 用户连打十个字符，磁盘只被碰一次（§11.3）。
    /// </summary>
    public void ScheduleSave(Guid noteId)
    {
        if (!_pending.TryGetValue(noteId, out IUiTimer? timer))
        {
            timer = _timers.Create();
            _pending[noteId] = timer;
        }

        timer.Start(TimeSpan.FromMilliseconds(DelayMilliseconds), () => OnTimerDue(noteId));
    }

    /// <summary>
    /// 取消等待中的那一轮。
    /// </summary>
    /// <remarks>
    /// 便签窗口关闭时必须调用（§18.3）：少了它，窗口关了还会在 500 毫秒后触发一次保存，
    /// 而那时 ViewModel 已经被释放。
    /// </remarks>
    public void CancelScheduledSave(Guid noteId)
    {
        if (_pending.TryGetValue(noteId, out IUiTimer? timer))
        {
            timer.Stop();
        }
    }

    /// <summary>立刻保存，绕过去抖。关单张便签的窗口时用。</summary>
    public async Task SaveNowAsync(Guid noteId)
    {
        CancelScheduledSave(noteId);

        await SaveAsync(noteId);
    }

    /// <summary>
    /// 把还在等待的全部立刻落盘。退出流程用（§17.4）。
    /// </summary>
    /// <remarks>
    /// 先取一份 id 快照再遍历：<see cref="SaveAsync"/> 内部会把定时器停下来，
    /// 期间若有回调又调了 <see cref="ScheduleSave"/>，直接遍历字典会抛
    /// <c>Collection was modified</c>。
    /// </remarks>
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        Guid[] pending = [.. _pending.Keys];

        foreach (Guid noteId in pending)
        {
            ct.ThrowIfCancellationRequested();

            await SaveAsync(noteId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        foreach (IUiTimer timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
        _isDisposed = true;
    }

    /// <summary>
    /// 定时器到期：发出去就不等。
    /// </summary>
    /// <remarks>
    /// 敢这么写是因为 <see cref="SaveAsync"/> 自己吞掉全部异常——
    /// 否则这里会留下一个谁也没观察到的 <see cref="Task"/>，
    /// 保存失败将完全静默（§11.5 要求至少留下日志）。
    /// </remarks>
    private void OnTimerDue(Guid noteId) => _ = SaveAsync(noteId);

    private async Task SaveAsync(Guid noteId)
    {
        CancelScheduledSave(noteId);

        try
        {
            await _noteService.SaveNoteAsync(noteId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 合同里最可能出现的两类失败：文件被别的程序占用、没有写权限。
            // 记一笔就够——用户的编辑内容还在内存里，下一次改动会再排一轮保存。
            _logger.LogWarning(
                "自动保存失败：{NoteId}（{ExceptionType}）。内容仍在内存中，下次编辑会重试。",
                noteId,
                ex.GetType().Name);
        }
    }
}
