using LumiMemo.App.Abstractions;
using LumiMemo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Services;

/// <summary>
/// 把「已经被接住的未处理异常」告诉用户，并对同一个异常类型做节流（§17.5 的第 1 层）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么要节流</strong>：UI 线程的未处理异常大多来自绑定或命令，
/// 它们往往成串出现——一个每帧都失败的绑定能在几百毫秒里抛出上千次。
/// 每来一次就弹一个模态框，用户会陷在一堆关不完的对话框里，
/// 那比异常本身更让人没法用（§17.5）。
/// </para>
/// <para>
/// 两条规则，都按<strong>异常类型</strong>分别记账：
/// </para>
/// <list type="number">
///   <item>同一个类型在 10 秒内只报一次。</item>
///   <item>一轮之内报满 5 次之后就不再弹 UI，只写日志。</item>
/// </list>
/// <para>
/// 「一轮」的边界文档没定。这里取<strong>静默 60 秒即翻篇</strong>，
/// 而不是「整个会话一共 5 次」：后者听着更像那两句话的字面意思，
/// 但代价是几小时之后才出现的另一桩真事故，会被早高峰期的计数永久盖掉——
/// 而弹窗的全部意义正是「有异常发生了」。静默够久说明前面那阵子已经过去了。
/// </para>
/// <para>
/// <strong>只在 UI 线程上被调用</strong>（它唯一的调用点是 <c>DispatcherUnhandledException</c>），
/// 所以账本是一个普通 <see cref="Dictionary{TKey,TValue}"/>，不加锁。
/// </para>
/// <para>
/// <strong>它自己绝不能再抛</strong>。抛出去就是转着圈回到同一个处理器，
/// 提示框本身出问题时只能记一笔了事。
/// </para>
/// </remarks>
public sealed class ErrorReporter
{
    /// <summary>同一个异常类型的静默窗口（§17.5）。</summary>
    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(10);

    /// <summary>静默超过它就算新的一轮，计数归零。</summary>
    private static readonly TimeSpan BurstResetWindow = TimeSpan.FromSeconds(60);

    /// <summary>一轮之内最多弹几次框（§17.5）。</summary>
    private const int MaxReportsPerBurst = 5;

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ErrorReporter> _logger;

    private readonly Dictionary<Type, Counters> _history = [];

    /// <summary>正在弹框。提示框自己出问题时会再走进来，得挡住。</summary>
    private bool _reporting;

    public ErrorReporter(
        IAppPaths paths,
        IClock clock,
        IDialogService dialogs,
        ILogger<ErrorReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _clock = clock;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>
    /// 记下这个异常；轮到该报的时候弹一个框告诉用户。
    /// </summary>
    /// <param name="exception">已经被接住的异常。</param>
    /// <remarks>
    /// 无论报不报，<strong>都会写日志</strong>：节流省掉的是打扰，不是证据。
    /// 日志带上异常对象本身（<c>ToString()</c>，含堆栈）——这是对 §19.5
    /// 「只记 <c>ex.GetType().Name</c>」的一处<strong>刻意破例</strong>：
    /// 那些规则是为了不让便签正文顺手漏进日志，而这里是异常已经逃到进程边界上的时刻，
    /// 堆栈是判断「到底哪一行炸了」唯一的线索，不记就等于什么都没留下。
    /// 日志落在本机 <c>%LOCALAPPDATA%</c>，不外发（§20.5）。
    /// </remarks>
    public void ReportRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Type type = exception.GetType();

        _logger.LogError(exception, "已接住的未处理异常：{Type}", type.FullName);

        if (!TryTakeReportSlot(type))
        {
            return;
        }

        _ = ShowAsync(type, exception);
    }

    private async Task ShowAsync(Type type, Exception exception)
    {
        if (_reporting)
        {
            return;
        }

        _reporting = true;

        try
        {
            await _dialogs.ShowErrorAsync("程序遇到了一个问题", BuildMessage(type, exception));
        }
        catch (Exception ex)
        {
            // 见类说明：到这里已经没有更上一层可以托付了。
            _logger.LogError(ex, "错误提示框本身失败：{Type}", ex.GetType().Name);
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>
    /// 现在轮得到这个类型弹框吗？轮到就记账并返回 <see langword="true"/>。
    /// </summary>
    private bool TryTakeReportSlot(Type type)
    {
        DateTimeOffset now = _clock.Now;

        if (!_history.TryGetValue(type, out Counters previous))
        {
            _history[type] = new Counters(now, 1);

            return true;
        }

        TimeSpan sinceLastReport = now - previous.LastReportedAt;

        // 窗口内被压掉的那几次不更新 LastReportedAt：压掉的不是「报告」，
        // 更新它等于把窗口一直往后推，成串的异常就永远报不出来了。
        if (sinceLastReport < ReportWindow)
        {
            return false;
        }

        int count = sinceLastReport >= BurstResetWindow ? 0 : previous.Count;

        if (count >= MaxReportsPerBurst)
        {
            return false;
        }

        _history[type] = new Counters(now, count + 1);

        return true;
    }

    /// <summary>
    /// 拼提示文案。三段：怎么回事、异常自己说了什么、去哪里看完整的。
    /// </summary>
    /// <remarks>
    /// 只给日志<strong>目录</strong>，不给具体文件名：那个目录里只放本程序的日志，
    /// 而文件名带序号与滚动槽号，写死一个反而会指错文件。
    /// </remarks>
    private string BuildMessage(Type type, Exception exception) =>
        $"""
        程序遇到了一个问题，已经跳过出错的这一步继续运行。
        刚才那一步可能没有完成，检查一下再做一次。

        {type.FullName}
        {exception.Message}

        完整信息记在日志里：
        {_paths.LogDirectory}
        """;

    /// <summary>某个异常类型的记账：上一次报告的时刻，以及这一轮报了几次。</summary>
    private readonly record struct Counters(DateTimeOffset LastReportedAt, int Count);
}
