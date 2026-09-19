using System.Globalization;
using System.Text;
using System.Threading.Channels;
using LumiMemo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Logging;

/// <summary>
/// 把日志写进 <c>%LOCALAPPDATA%\LumiMemo\logs\lumimemo-N.log</c>（§20.5）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它只做磁盘那一半。</strong>格式（哪一行长什么样）与过滤（哪些级别要记）
/// 在 <c>Microsoft.Extensions.Logging</c> 那一侧，本类接收的是已格式化好的正文。
/// </para>
/// <para>
/// <strong>写入是异步批量的</strong>，这是 §20.5 对日志的硬要求：调用方（可能在 UI 线程上、
/// 可能在一次写盘途中）只把整行塞进一个通道就返回，真正的 IO 由一条后台线程每 20 条
/// 或每 250 毫秒成批做一次。逐条 <c>File.AppendAllText</c> 会产生大量小写入，
/// 那是文档里点名要避免的写法。
/// </para>
/// <para>
/// <strong>滚动按大小而不是按日期</strong>：文件是 <c>lumimemo-1.log</c> 到
/// <c>lumimemo-5.log</c> 五个固定槽，一个槽写满 5MB 就换到下一个，环形用满之后
/// 回到 1 号、把那里的老内容清掉（§20.5「单文件 5MB，滚动保留最近 5 个」）。
/// 槽名固定而内容在推进，用户按文件的修改时间找最新的那个即可——
/// 所以每条日志都带完整时间戳，而不是靠文件名里的日期。
/// </para>
/// <para>
/// <strong>写盘失败一律吞掉</strong>：日志写不进去（磁盘满、目录被占、权限被改）
/// 只该让日志少几行，绝不能把程序本身上升成一个异常——那会让「日志坏了」变成
/// 「程序坏了」。这是全仓库唯一合理地吞掉所有异常的地方。
/// </para>
/// <para>
/// <strong>它不替调用方过滤敏感内容</strong>（§19.5）：正文、标签列表这些不许进日志的
/// 东西，责任在调用点。仓库里所有日志调用都只记 <c>ex.GetType().Name</c> 而不是异常对象，
/// 原因就在这里——本类收到异常对象会把它的 Message 原样写出去。
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>单个日志文件的大小上限（§20.5）。</summary>
    public const long DefaultMaxFileBytes = 5L * 1024 * 1024;

    /// <summary>环形保留的文件个数（§20.5）。</summary>
    public const int DefaultMaxFiles = 5;

    /// <summary>攒够这么多条就立刻落盘，不等定时器（§20.5）。</summary>
    public const int BatchSize = 20;

    /// <summary>攒不满一批时最多等这么久（§20.5）。</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>退出时等最后一批写完的上限。</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    /// <summary>通道容量。满了丢最旧的：留最新的是排查问题的常识。</summary>
    private const int QueueCapacity = 1000;

    /// <summary>文件名的公共前缀，测试与用户按它找文件。</summary>
    public const string FileNamePrefix = "lumimemo-";

    /// <summary>UTF-8 带 BOM：记事本打开中文日志不会乱码（每个文件只在开头写一次）。</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _logDirectory;
    private readonly IClock _clock;
    private readonly LogLevel _minimumLevel;
    private readonly TimeSpan _flushInterval;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly Channel<string> _channel;
    private readonly Task _consumer;
    private int _disposed;

    /// <summary>本进程当前在写的槽（1 起数）；0 表示还没定过，只在消费线程上改。</summary>
    private int _slot;

    /// <param name="logDirectory">日志目录，不存在时自己建。</param>
    /// <param name="clock">时间来源。每行的时间戳都取自它，测试因此能钉死时间。</param>
    /// <param name="minimumLevel">低于它的级别直接丢掉。</param>
    /// <param name="flushInterval">攒不满一批时的等待上限。</param>
    /// <param name="maxFileBytes">单文件上限。</param>
    /// <param name="maxFiles">环形槽个数。</param>
    public FileLoggerProvider(
        string logDirectory,
        IClock clock,
        LogLevel minimumLevel = LogLevel.Information,
        TimeSpan? flushInterval = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);

        _logDirectory = logDirectory;
        _clock = clock;
        _minimumLevel = minimumLevel;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        // 刻意走 Task.Run：这样消费循环里任何一个 await 的续体都落在池线程上，
        // 不会去捕捕获到 UI 线程的同步上下文（§20.4 要求日志写入在后台线程）。
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>把还在通道里的日志写完再走。</summary>
    /// <remarks>
    /// 容器在 <c>App.OnExit</c> 里释放它，而释放发生在 <c>ShutdownAndWait</c> 之后——
    /// 也就是说退出前那几条（正好是最常出问题的一段，§17.4）还在通道里，
    /// 不等一下就会连同进程一起消失。
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            _consumer.Wait(DisposeWait);
        }
        catch (AggregateException)
        {
            // 消费线程自己出的问题在 ConsumeAsync 里已经吞过了；这里只负责别把
            // 退出流程卡住。
        }
    }

    /// <summary>不断把通道里的行成批取出来写盘，直到通道被关闭且取空。</summary>
    /// <remarks>
    /// 一批的结束条件有两个，谁先到算谁（§20.5）：攒满 <see cref="BatchSize"/> 条，
    /// 或者从这一批的头一条算起过了 <c>flushInterval</c>。少了前者，突发的一批日志
    /// 会白等一个间隔；少了后者，一条孤零零的日志要等到下一批才落盘——
    /// 而「刚写完那条正好是排查要用的」是常态。
    /// </remarks>
    private async Task ConsumeAsync()
    {
        ChannelReader<string> reader = _channel.Reader;
        var batch = new List<string>(BatchSize);

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                Task deadline = Task.Delay(_flushInterval);

                while (batch.Count < BatchSize)
                {
                    if (reader.TryRead(out string? line))
                    {
                        batch.Add(line);

                        continue;
                    }

                    // 通道空了：等「有新行」或「这一批到点」，哪个先来。
                    Task<bool> more = reader.WaitToReadAsync().AsTask();

                    if (await Task.WhenAny(more, deadline).ConfigureAwait(false) == deadline)
                    {
                        break;
                    }

                    if (!await more.ConfigureAwait(false))
                    {
                        break;
                    }
                }

                WriteBatch(batch);
                batch.Clear();
            }
        }
        catch (Exception)
        {
            // 消费线程一旦退出，之后所有日志都会堵在通道里（有界，不会吃光内存），
            // 而「没有日志」恰恰是最难查的一类故障。能到这里的是通道本身的问题，
            // 只能记在「别让日志把程序拖垮」这一条上把它吞掉。
        }

        // 收尾：异常路径下可能还剩一批没写。
        while (reader.TryRead(out string? rest))
        {
            batch.Add(rest);

            if (batch.Count >= BatchSize)
            {
                WriteBatch(batch);
                batch.Clear();
            }
        }

        WriteBatch(batch);
    }

    /// <summary>
    /// 把一批行写进当前该写的那个文件。
    /// </summary>
    /// <remarks>
    /// 一批一次打开、一次关闭：§20.5 要的「批量」就体现在这里。
    /// 打开时带 <see cref="FileShare.Read"/>，这样用户能一边用记事本看、
    /// 程序一边继续写。
    /// </remarks>
    private void WriteBatch(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);

            (string path, bool truncate) = PickTargetFile();

            using var stream = new FileStream(
                path,
                truncate ? FileMode.Create : FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8);

            foreach (string line in lines)
            {
                writer.WriteLine(line);
            }
        }
        catch (Exception)
        {
            // 见类说明：日志写不进去不能影响程序本身。
        }
    }

    /// <summary>
    /// 挑这一批该落进哪个槽：本进程记着的那个槽还有空间就接着写，
    /// 写满了就换到下一个（环形回到开头时，那个槽里的老内容会被清掉）。
    /// </summary>
    /// <remarks>
    /// <strong>当前槽记在内存里，不靠文件时间戳推。</strong>按「谁最新」挑槽看着更聪明，
    /// 但文件时间戳的精度是系统计时器那一档（十几毫秒），一个 5MB 的槽写满得很快时，
    /// 两个槽的时间戳会撞在一起——那时「最新」与「最旧」都退化成「第一个找到的」，
    /// 于是程序会在两个槽之间来回覆盖，另外几个槽形同虚设。
    /// 时间戳只在进程刚起来、还不知道该接着谁写的时候用一次（见 <see cref="ResumeSlot"/>）。
    /// </remarks>
    /// <returns>目标文件，以及是否要先清空它。</returns>
    private (string Path, bool Truncate) PickTargetFile()
    {
        if (_slot == 0)
        {
            _slot = ResumeSlot();
        }

        string path = SlotPath(_slot);
        var file = new FileInfo(path);

        if (!file.Exists || file.Length < _maxFileBytes)
        {
            return (path, false);
        }

        _slot = (_slot % _maxFiles) + 1;

        string next = SlotPath(_slot);

        return (next, File.Exists(next));
    }

    /// <summary>接着上一次运行留下的最新那个槽写；一个日志文件都没有时从 1 号开始。</summary>
    private int ResumeSlot()
    {
        int slot = 0;
        DateTime newestTime = DateTime.MinValue;
        long newestLength = 0;

        for (int index = 1; index <= _maxFiles; index++)
        {
            var file = new FileInfo(SlotPath(index));

            if (!file.Exists)
            {
                continue;
            }

            // 时间戳撞车时用「谁更长」当第二判据：同一个槽被清空重写之后是最短的，
            // 因此这个方向把「刚清空过的那一个」推到最后，选中的更可能是真在被写的那个。
            // 撞车本身无害（只是接着谁写的选择不唯一），这里的判据只是让它别系统性选错。
            if (slot == 0 || file.LastWriteTimeUtc > newestTime
                || (file.LastWriteTimeUtc == newestTime && file.Length > newestLength))
            {
                slot = index;
                newestTime = file.LastWriteTimeUtc;
                newestLength = file.Length;
            }
        }

        return slot == 0 ? 1 : slot;
    }

    private string SlotPath(int index) =>
        Path.Combine(_logDirectory, $"{FileNamePrefix}{index}.log");

    /// <summary>把一条日志渲染成一行（异常会带在下面几行里）。</summary>
    private string FormatLine(LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder(message.Length + 96);

        builder
            .Append(_clock.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(level)
            .Append("] ")
            .Append(category)
            .Append(": ")
            .Append(message);

        if (exception is not null)
        {
            // 用 ToString() 而不是 Message：堆栈是排查问题时另一半证据。
            // 代价是 Message 会被原样写出——调用方按 §19.5 不许传可能内嵌用户内容的异常。
            foreach (string line in exception.ToString().Split('\n'))
            {
                builder.AppendLine().Append("    ").Append(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="ILogger"/> 的最小实现：只把渲染好的行排进通道。
    /// </summary>
    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            // 已经释放掉的 provider 不再收东西：容器释放它之后就只差进程退出了，
            // 这时排进去的行没人写，只会白占内存。
            if (Volatile.Read(ref provider._disposed) != 0)
            {
                return;
            }

            provider._channel.Writer.TryWrite(
                provider.FormatLine(logLevel, categoryName, formatter(state, exception), exception));
        }
    }
}
