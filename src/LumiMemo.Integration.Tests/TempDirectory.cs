using System.IO;

namespace LumiMemo.Integration.Tests;

/// <summary>
/// 每个测试独占的临时目录，用完自动删除（§21.3：「用 <see cref="IDisposable"/> 的临时目录 fixture，
/// 每个测试一个独立的目录树」）。
/// </summary>
/// <remarks>
/// 集成测试必须打真实文件系统——这正是它和单元测试的分界。
/// 但也因此绝不能让测试碰到用户的真实笔记目录或 <c>%LOCALAPPDATA%</c>：
/// 每个测试一个全新的临时目录，并且它是唯一的落点。
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    private bool _disposed;

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LumiMemo.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>本测试独占的根目录。</summary>
    public string Path { get; }

    /// <summary>在根目录下取一个子目录的完整路径（不创建它）。</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>在根目录下取一个子目录的完整路径并创建它。</summary>
    public string CreateDirectory(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// 删除整个目录树。
    /// </summary>
    /// <remarks>
    /// 删除失败被吞掉是刻意的：Windows 上杀毒软件、索引器或还没释放的文件句柄
    /// 都可能导致 <c>Directory.Delete</c> 抛异常。为此让一个已经通过了断言的测试变红，
    /// 只会掩盖真正的失败。残留的临时目录由系统清理。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _disposed = true;
    }
}
