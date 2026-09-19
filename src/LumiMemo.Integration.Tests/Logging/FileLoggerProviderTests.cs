using System.IO;
using LumiMemo.Infrastructure.Logging;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LumiMemo.Integration.Tests.Logging;

/// <summary>
/// <see cref="FileLoggerProvider"/> 的集成测试（§20.5）。
/// </summary>
/// <remarks>
/// <para>
/// 它天生是异步的（后台线程 + 批量 + 定时器），因此这里绝大多数用例的「等」都有两种写法：
/// <strong>等它自己到点</strong>（<see cref="WaitForAsync"/> 轮询）与
/// <strong>等它收尾</strong>（<c>Dispose</c> 会把通道里的剩余批次写完）。
/// 后一种是确定性的，能用就用；只有「不 Dispose 就该落盘」那几条才走轮询——
/// 那正是它们要证明的事。
/// </para>
/// <para>
/// 另有一类用例专门盯着「失败时也不许抛」：日志坏掉不能把程序带崩，
/// 所以写不进去时构造函数与 <c>Dispose</c> 都必须安然返回。
/// </para>
/// </remarks>
public sealed class FileLoggerProviderTests
{
    /// <summary>固定时刻：时间戳因此可以被断言，而不是只能看个大概。</summary>
    private static readonly DateTimeOffset When =
        new(2026, 9, 19, 13, 7, 9, TimeSpan.FromHours(8));

    /// <summary>短得不会拖慢测试的刷新间隔。</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(30);

    /// <summary>长得在测试里绝不会到期的刷新间隔：用来证明「是批次满了才落盘」。</summary>
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 写入的日志能读到时间戳级别类别与正文()
    {
        using var temp = new TempDirectory();
        using var provider = Create(temp.Path, Quick);

        provider.CreateLogger("LumiMemo.Test").LogInformation(
            "笔记目录：本次使用 {Folder}。",
            @"C:\Users\Ning\Desktop\新建文件夹 (4)");

        string log = await WaitForAsync(temp.Path, text => text.Contains("笔记目录"));

        Assert.Contains("2026-09-19 13:07:09.000 +08:00", log);
        Assert.Contains("[Information]", log);
        Assert.Contains("LumiMemo.Test: ", log);
        Assert.Contains(@"C:\Users\Ning\Desktop\新建文件夹 (4)", log);
    }

    [Fact]
    public async Task 低于最小级别的被丢掉()
    {
        using var temp = new TempDirectory();
        using var provider = new FileLoggerProvider(
            temp.Path, new FakeClock(When), LogLevel.Warning, Quick);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        logger.LogDebug("调试的不该出现");
        logger.LogInformation("信息的不该出现");
        logger.LogWarning("警告的该出现");

        string log = await WaitForAsync(temp.Path, text => text.Contains("警告的该出现"));

        Assert.DoesNotContain("调试的不该出现", log);
        Assert.DoesNotContain("信息的不该出现", log);
        Assert.Contains("[Warning]", log);
    }

    [Fact]
    public async Task 攒满一批就落盘_不等定时器()
    {
        using var temp = new TempDirectory();
        using var provider = Create(temp.Path, Never);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        for (int index = 0; index < FileLoggerProvider.BatchSize; index++)
        {
            logger.LogInformation("第{Index}条", index);
        }

        // 间隔是十分钟，能读到就只可能是「攒够一批」这条路径。
        string log = await WaitForAsync(
            temp.Path,
            text => text.Contains($"第{FileLoggerProvider.BatchSize - 1}条"));

        Assert.Contains("第0条", log);
    }

    [Fact]
    public async Task 不满一批的等到间隔到期才落盘()
    {
        using var temp = new TempDirectory();
        using var provider = Create(temp.Path, Quick);

        provider.CreateLogger("LumiMemo.Test").LogInformation("孤零零的一条");

        string log = await WaitForAsync(temp.Path, text => text.Contains("孤零零的一条"));

        Assert.Contains("孤零零的一条", log);
    }

    [Fact]
    public void 释放时把还没落盘的那一批写完()
    {
        using var temp = new TempDirectory();

        // 间隔十分钟且不轮询：只有 Dispose 把它写完，这三条才读得到。
        var provider = Create(temp.Path, Never);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        logger.LogInformation("退出前第一条");
        logger.LogInformation("退出前第二条");
        logger.LogInformation("退出前第三条");

        provider.Dispose();

        string log = ReadAll(temp.Path);

        Assert.Contains("退出前第一条", log);
        Assert.Contains("退出前第二条", log);
        Assert.Contains("退出前第三条", log);
    }

    [Fact]
    public void 异常的文字跟着那一行一起落盘且缩进()
    {
        using var temp = new TempDirectory();

        var provider = Create(temp.Path, Quick);

        provider.CreateLogger("LumiMemo.Test").LogError(
            new InvalidOperationException("磁盘上的那个文件被别的程序占着"),
            "写便签失败。");

        provider.Dispose();

        string log = ReadAll(temp.Path);

        Assert.Contains("写便签失败。", log);
        Assert.Contains("System.InvalidOperationException", log);
        Assert.Contains("磁盘上的那个文件被别的程序占着", log);

        // 堆栈每一行都缩进四格，好让「哪几行属于同一条日志」一眼看得出来。
        string[] lines = log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.All(
            lines.Skip(1),
            line => Assert.StartsWith("    ", line));
    }

    [Fact]
    public void 日志目录不存在时自己建()
    {
        using var temp = new TempDirectory();
        string logDirectory = temp.Combine("logs");

        Assert.False(Directory.Exists(logDirectory));

        var provider = Create(logDirectory, Quick);

        provider.CreateLogger("LumiMemo.Test").LogInformation("第一条");
        provider.Dispose();

        Assert.True(Directory.Exists(logDirectory));
        Assert.Contains("第一条", ReadAll(logDirectory));
    }

    [Fact]
    public void 写不进去时既不抛异常也不动那个同名文件()
    {
        using var temp = new TempDirectory();

        // 把「日志目录」指到一个已经存在的文件上：建目录会失败，写文件自然也无从谈起。
        string blocked = temp.Combine("被占住的名字");
        File.WriteAllText(blocked, "这里的原内容不能被日志动到");

        var provider = Create(blocked, Quick);

        provider.CreateLogger("LumiMemo.Test").LogInformation("这条注定写不下去");
        provider.Dispose();

        Assert.Equal("这里的原内容不能被日志动到", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task 一个槽写满就换到下一个槽()
    {
        using var temp = new TempDirectory();
        using var provider = new FileLoggerProvider(
            temp.Path, new FakeClock(When), LogLevel.Information, Quick,
            maxFileBytes: 256,
            maxFiles: 3);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        // 一批 20 条一起写进去，无论上限多小都必然写满那一个槽——
        // 批量是「一个整体」，不会为了不超上限把它切两半分两个槽。
        WriteBurst(logger, "第一批");
        await WaitForAsync(temp.Path, text => text.Contains("第一批-19"));

        WriteBurst(logger, "第二批");

        string log = await WaitForAsync(temp.Path, text => text.Contains("第二批-19"));

        Assert.Contains("第一批-19", log);
        Assert.Equal(2, LogFiles(temp.Path).Length);
    }

    [Fact]
    public async Task 槽用满之后回到开头并清掉老内容()
    {
        using var temp = new TempDirectory();
        using var provider = new FileLoggerProvider(
            temp.Path, new FakeClock(When), LogLevel.Information, Quick,
            maxFileBytes: 256,
            maxFiles: 2);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        WriteBurst(logger, "第一批");
        await WaitForAsync(temp.Path, text => text.Contains("第一批-19"));

        WriteBurst(logger, "第二批");
        await WaitForAsync(temp.Path, text => text.Contains("第二批-19"));

        WriteBurst(logger, "第三批");

        string log = await WaitForAsync(temp.Path, text => text.Contains("第三批-19"));

        Assert.Contains("第二批-19", log);
        Assert.DoesNotContain("第一批", log);
    }

    [Fact]
    public void 每个文件只写一个字节序标记()
    {
        using var temp = new TempDirectory();

        var provider = Create(temp.Path, Quick);

        ILogger logger = provider.CreateLogger("LumiMemo.Test");

        // 分两批写同一个文件：BOM 只该在文件开头出现一次。
        WriteBurst(logger, "第一批");
        WriteBurst(logger, "第二批");

        provider.Dispose();

        foreach (string file in LogFiles(temp.Path))
        {
            Assert.Equal(1, CountUtf8Bom(File.ReadAllBytes(file)));
        }
    }

    private static FileLoggerProvider Create(
        string logDirectory,
        TimeSpan flushInterval) =>
        new(logDirectory, new FakeClock(When), LogLevel.Information, flushInterval);

    /// <summary>写满一批（<see cref="FileLoggerProvider.BatchSize"/> 条），后缀用来分辨是哪一批。</summary>
    private static void WriteBurst(ILogger logger, string label)
    {
        for (int index = 0; index < FileLoggerProvider.BatchSize; index++)
        {
            logger.LogInformation("{Label}-{Index}", label, index);
        }
    }

    private static string[] LogFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{FileLoggerProvider.FileNamePrefix}*.log")
            : [];

    /// <summary>把目录里所有日志文件按修改时间顺序拼起来；一个都没有时返回空串。</summary>
    private static string ReadAll(string directory)
    {
        var builder = new System.Text.StringBuilder();

        foreach (string file in LogFiles(directory).OrderBy(File.GetLastWriteTimeUtc))
        {
            try
            {
                builder.Append(File.ReadAllText(file));
            }
            catch (IOException)
            {
                // 轮询期间正好撞上换槽（那个文件被清空或删掉）：忽略这次，下一轮再读。
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 轮询等日志落盘，最多等五秒；返回最后一次读到的内容（等到了就是完整的）。
    /// </summary>
    /// <remarks>
    /// 断言留给调用方做，这里只负责「等」：失败时 <c>Assert.Contains</c> 会把实际内容
    /// 一起打出来，比在这里抛一个「超时了」信息更全。
    /// </remarks>
    private static async Task<string> WaitForAsync(string directory, Func<string, bool> until)
    {
        string text = ReadAll(directory);

        for (int attempt = 0; attempt < 200 && !until(text); attempt++)
        {
            await Task.Delay(25, Ct);

            text = ReadAll(directory);
        }

        return text;
    }

    private static int CountUtf8Bom(byte[] bytes)
    {
        int count = 0;

        for (int index = 0; index + 2 < bytes.Length; index++)
        {
            if (bytes[index] == 0xEF && bytes[index + 1] == 0xBB && bytes[index + 2] == 0xBF)
            {
                count++;
            }
        }

        return count;
    }
}
