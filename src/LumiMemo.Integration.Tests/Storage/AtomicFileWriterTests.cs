using System.IO;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Integration.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="AtomicFileWriter"/> 的集成测试（§11.2）。
/// </summary>
/// <remarks>
/// 原子性本身没法在单元测试里证明（要真断电才算数），但可以钉住它的几条可观测后果：
/// 内容写全了、换名换成了、失败了不留垃圾、清理能递归。
/// </remarks>
public sealed class AtomicFileWriterTests
{
    private static readonly DateTimeOffset When =
        new(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>取消令牌。xunit.v3 要求显式传递（xUnit1051），这里统一取当前测试的。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 写入新文件_内容与修改时间都对()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("新便签.md");
        var writer = new AtomicFileWriter(new FakeClock());

        await writer.WriteAsync(path, "# 标题\r\n"u8.ToArray(), When, Ct);

        Assert.Equal("# 标题\r\n", File.ReadAllText(path));

        // §11.2：写完之后必须把修改时间设回便签自己的时间，否则外部编辑器与同步工具
        // 看到的都是「刚刚」——那是临时文件的创建时间。
        TimeSpan drift = File.GetLastWriteTimeUtc(path) - When.UtcDateTime;
        Assert.True(drift.Duration() < TimeSpan.FromSeconds(1), $"修改时间偏差过大：{drift}");
    }

    [Fact]
    public async Task 覆盖已有文件_内容被整体替换()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("便签.md");
        File.WriteAllText(path, "旧内容旧内容旧内容");
        var writer = new AtomicFileWriter(new FakeClock());

        await writer.WriteAsync(path, "新"u8.ToArray(), When, Ct);

        Assert.Equal("新", File.ReadAllText(path));
    }

    [Fact]
    public async Task 写完之后没有残留的临时文件()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("便签.md");
        var writer = new AtomicFileWriter(new FakeClock());

        await writer.WriteAsync(path, "内容"u8.ToArray(), When, Ct);

        Assert.Empty(Directory.GetFiles(temp.Path, "*" + AtomicFileWriter.TempSuffix));
    }

    [Fact]
    public async Task 目录不存在时自动创建()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("工作", "周报", "便签.md");
        var writer = new AtomicFileWriter(new FakeClock());

        await writer.WriteAsync(path, "内容"u8.ToArray(), When, Ct);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task 目标文件只读时_写入失败但原文件内容一字未动且不留垃圾()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("便签.md");
        File.WriteAllText(path, "用户的原始内容");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            // 关掉等待，否则白白睡掉 1 秒。
            var writer = new AtomicFileWriter(new FakeClock(), retryDelaysMilliseconds: []);

            await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(path, "新内容"u8.ToArray(), When, Ct));

            // 这才是原子写的全部意义：写失败了，用户的数据还在。
            Assert.Equal("用户的原始内容", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(temp.Path, "*" + AtomicFileWriter.TempSuffix));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void 清理残留临时文件_递归整棵树()
    {
        using var temp = new TempDirectory();
        string nested = temp.CreateDirectory("工作", "周报");
        string first = temp.Combine("根.md.20260919120000123-a1b2c3d4" + AtomicFileWriter.TempSuffix);
        string second = System.IO.Path.Combine(nested, "深.md.20260919120000124-b2c3d4e5" + AtomicFileWriter.TempSuffix);
        string keeper = temp.Combine("正常便签.md");

        File.WriteAllText(first, "残骸一");
        File.WriteAllText(second, "残骸二");
        File.WriteAllText(keeper, "别动我");

        int deleted = AtomicFileWriter.CleanupOrphanedTempFiles(temp.Path);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public void 清理时目录不存在_安静返回零()
    {
        using var temp = new TempDirectory();

        Assert.Equal(0, AtomicFileWriter.CleanupOrphanedTempFiles(temp.Combine("从来没有过")));
    }

    [Fact]
    public void 临时文件后缀与文档一致()
    {
        Assert.Equal(".lumitmp", AtomicFileWriter.TempSuffix);
    }
}
