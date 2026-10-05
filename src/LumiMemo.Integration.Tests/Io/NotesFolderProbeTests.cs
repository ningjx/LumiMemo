using LumiMemo.Infrastructure.Io;
using Xunit;

namespace LumiMemo.Integration.Tests.Io;

/// <summary>
/// <see cref="NotesFolderProbe"/> 的集成测试：用户选的目录真的能当笔记目录用吗。
/// </summary>
public sealed class NotesFolderProbeTests
{
    [Fact]
    public void 已存在的可写目录_通过()
    {
        using var temp = new TempDirectory();

        Assert.Null(NotesFolderProbe.TryEnsureUsable(temp.Path));
    }

    [Fact]
    public void 不存在的目录_被创建并通过()
    {
        using var temp = new TempDirectory();
        string nested = temp.Combine("还没建", "更深的");

        Assert.Null(NotesFolderProbe.TryEnsureUsable(nested));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void 探测不留下文件()
    {
        using var temp = new TempDirectory();

        NotesFolderProbe.TryEnsureUsable(temp.Path);

        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void 路径指向一个已存在的文件_报错()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("这不是目录.txt");
        File.WriteAllText(file, "");

        string? error = NotesFolderProbe.TryEnsureUsable(file);

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空路径_报错(string? path)
    {
        Assert.NotNull(NotesFolderProbe.TryEnsureUsable(path));
    }
}
