using LumiMemo.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="NotesFolderCopier"/> 的集成测试：换目录时的便签搬运。
/// </summary>
public sealed class NotesFolderCopierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 复制_便签全部过去且内容一致()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        string destination = temp.Combine("目标");
        WriteNote(source, "a", "甲");
        WriteNote(source, "b", "乙");

        CopyResult result = await Copier().CopyAsync(source, destination, Ct);

        Assert.Equal(new CopyResult(2, 0, 0), result);
        Assert.Equal("甲", File.ReadAllText(Path.Combine(destination, "a.lumi")));
        Assert.Equal("乙", File.ReadAllText(Path.Combine(destination, "b.lumi")));
    }

    [Fact]
    public async Task 复制_源文件一个都不动()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        string destination = temp.Combine("目标");
        WriteNote(source, "a", "甲");

        await Copier().CopyAsync(source, destination, Ct);

        Assert.True(File.Exists(Path.Combine(source, "a.lumi")));
    }

    [Fact]
    public async Task 同名已存在_跳过且不覆盖目标那份()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        string destination = temp.CreateDirectory("目标");
        WriteNote(source, "same", "旧内容");
        WriteNote(destination, "same", "新内容");

        CopyResult result = await Copier().CopyAsync(source, destination, Ct);

        Assert.Equal(new CopyResult(0, 1, 0), result);
        Assert.Equal("新内容", File.ReadAllText(Path.Combine(destination, "same.lumi")));
    }

    [Fact]
    public async Task 目标目录不存在_自动创建()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        string destination = temp.Combine("还不存在", "目标");
        WriteNote(source, "a", "甲");

        CopyResult result = await Copier().CopyAsync(source, destination, Ct);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(destination, "a.lumi")));
    }

    [Fact]
    public async Task 源目录不存在_什么都不做()
    {
        using var temp = new TempDirectory();
        string destination = temp.Combine("目标");

        CopyResult result = await Copier().CopyAsync(temp.Combine("没有这个目录"), destination, Ct);

        Assert.Equal(new CopyResult(0, 0, 0), result);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task 非便签文件与元数据目录_都不碰()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        string destination = temp.CreateDirectory("目标");
        WriteNote(source, "a", "甲");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "我不是便签");

        // 元数据目录里的 .lumi（回收站那份）不该被当成便签搬走。
        string trash = Directory.CreateDirectory(Path.Combine(source, ".lumimemo", "trash")).FullName;
        WriteNote(trash, "deleted", "回收站里的");

        CopyResult result = await Copier().CopyAsync(source, destination, Ct);

        Assert.Equal(new CopyResult(1, 0, 0), result);
        Assert.False(File.Exists(Path.Combine(destination, "readme.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "deleted.lumi")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".lumimemo")));
    }

    [Fact]
    public void 数便签_只数顶层的()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateDirectory("源");
        WriteNote(source, "a", "甲");
        WriteNote(source, "b", "乙");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "不算");

        string trash = Directory.CreateDirectory(Path.Combine(source, ".lumimemo", "trash")).FullName;
        WriteNote(trash, "deleted", "回收站里的");

        Assert.Equal(2, NotesFolderCopier.CountNotes(source));
    }

    [Fact]
    public void 数便签_目录不存在时为零()
    {
        using var temp = new TempDirectory();

        Assert.Equal(0, NotesFolderCopier.CountNotes(temp.Combine("没有这个目录")));
    }

    private static NotesFolderCopier Copier() => new(NullLogger<NotesFolderCopier>.Instance);

    private static void WriteNote(string folder, string name, string content) =>
        File.WriteAllText(Path.Combine(folder, $"{name}.lumi"), content);
}
