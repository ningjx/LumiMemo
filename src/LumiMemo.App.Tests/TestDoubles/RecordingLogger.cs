using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="ILogger"/> 的记录型替身：把日志原文留在内存里，用例自己去翻。
/// </summary>
/// <remarks>
/// <para>
/// 它有两个用处。一是<strong>断言某条日志确实发生了</strong>——服务在「没有别的办法
/// 告诉外界」的岔路口（首启对话框被取消、退回默认笔记目录）只留下一行 Information，
/// 那一行就是唯一可验的结果。
/// </para>
/// <para>
/// 二是<strong>把日志塞进失败信息里</strong>：像单实例的监听循环这种「跑在后台、
/// 出了事只记一条警告」的东西，异常在别处表现为「毫无反应」，不带日志的断言
/// 会让人从头猜起。
/// </para>
/// <para>
/// <strong>收集用并发队列</strong>，因为被观察的服务多半跑在后台线程上写日志，
/// 而断言发生在测试线程上。
/// </para>
/// </remarks>
public class RecordingLogger : ILogger
{
    /// <summary>所有日志，按 <c>「级别: 正文 | 异常」</c> 的形态排队。</summary>
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Messages.Enqueue($"{logLevel}: {formatter(state, exception)} | {exception}");
    }
}

/// <summary>
/// <see cref="ILogger{T}"/> 那一版的 <see cref="RecordingLogger"/>。
/// </summary>
/// <remarks>
/// 泛型与不泛型是两套互不相通的接口，而各个服务注入的是前者、容器释放路径拿到的
/// 是后者——两边指向同一批消息，所以这里只做继承而不是各写一份。
/// </remarks>
public sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;
