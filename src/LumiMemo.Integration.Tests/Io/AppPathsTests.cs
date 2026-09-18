using System.IO;
using LumiMemo.Infrastructure.Io;
using Xunit;

namespace LumiMemo.Integration.Tests.Io;

/// <summary>
/// <see cref="AppPaths"/> 的集成测试：打真实文件系统，但落在临时目录里（§21.3）。
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void 设备状态一律落在LocalAppData下_不跟随笔记目录()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);

        // §8.1：settings / layout / logs 必须固定在本机，否则 OneDrive 会把
        // A 机器的窗口坐标同步到 B 机器，窗口就跑到屏幕外去了。
        Assert.StartsWith(local.Path, paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(local.Path, paths.LayoutFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(local.Path, paths.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(local.Path, paths.RecoveryDirectory, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(notes.Path, paths.LayoutFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 回收站必须与笔记同卷_因此落在笔记目录内()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);

        // §8.1：同卷才能让 File.Move 保持原子。跨卷会退化成「复制 + 删除」，
        // 中途断电就是既没删干净、又没复制完整。
        Assert.StartsWith(notes.Path, paths.TrashDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(notes.Path, paths.TrashIndexFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 元数据目录名是点开头的隐藏目录()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);

        Assert.Equal(".lumimemo", AppPaths.MetadataDirectoryName);
        Assert.Contains(".lumimemo", paths.TrashDirectory, StringComparison.Ordinal);
        Assert.EndsWith("trash-index.json", paths.TrashIndexFile, StringComparison.Ordinal);
    }

    [Fact]
    public void 附件目录名可配置_默认是attachments()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);

        Assert.Equal(System.IO.Path.Combine(notes.Path, "attachments"), paths.AttachmentsDirectory);

        paths.SetAttachmentsFolderName("assets");

        Assert.Equal(System.IO.Path.Combine(notes.Path, "assets"), paths.AttachmentsDirectory);
    }

    [Fact]
    public void 尚未选定笔记目录时_NotesFolder是null()
    {
        using var local = new TempDirectory();

        var paths = new AppPaths(local.Path);

        // §8.6：首次启动还没选目录。这是合法状态，用 null 表达「没有」，
        // 而不是空串——空串会让派生路径变成相对路径。
        Assert.Null(paths.NotesFolder);

        // 设备状态那三个不受影响，它们本来就不依赖笔记目录。
        Assert.NotEmpty(paths.SettingsFile);
        Assert.NotEmpty(paths.LogDirectory);
    }

    [Fact]
    public void 尚未选定笔记目录时_问回收站路径直接抛异常()
    {
        using var local = new TempDirectory();

        var paths = new AppPaths(local.Path);

        // 关键点：必须抛，而不是返回 ".lumimemo\trash" 这种相对路径。
        // 后者会让调用方在进程当前目录下静默建出一棵目录树——
        // 从终端启动和双击 exe 启动的当前目录不同，笔记就散落在莫名其妙的地方了。
        Assert.Throws<InvalidOperationException>(() => paths.TrashDirectory);
        Assert.Throws<InvalidOperationException>(() => paths.TrashIndexFile);
        Assert.Throws<InvalidOperationException>(() => paths.AttachmentsDirectory);
    }

    [Fact]
    public void 设置相对路径的笔记目录_直接拒绝()
    {
        using var local = new TempDirectory();
        var paths = new AppPaths(local.Path);

        // 校验点在系统边界上：笔记目录来自 settings.json，
        // 那是用户能手改、还可能被网盘同步过来的文件。
        Assert.Throws<ArgumentException>(() => paths.SetNotesFolder(@"notes"));
        Assert.Throws<ArgumentException>(() => paths.SetNotesFolder(@"\notes"));
        Assert.Throws<ArgumentException>(() => paths.SetNotesFolder("   "));
    }

    [Fact]
    public void 建立设备状态目录_真的会在磁盘上出现()
    {
        using var local = new TempDirectory();

        var paths = new AppPaths(local.Combine("LumiMemo"));
        paths.EnsureLocalAppDataDirectories();

        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.True(Directory.Exists(paths.RecoveryDirectory));
    }

    [Fact]
    public void 建立设备状态目录_不碰笔记目录()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);
        paths.EnsureLocalAppDataDirectories();

        // 启动第一步跑在「用户还没选笔记目录」之前，此刻不该往笔记目录里写任何东西。
        Assert.Empty(Directory.EnumerateFileSystemEntries(notes.Path));
    }

    [Fact]
    public void 建立设备状态目录_重复调用是幂等的()
    {
        using var local = new TempDirectory();

        var paths = new AppPaths(local.Path);
        paths.EnsureLocalAppDataDirectories();
        paths.EnsureLocalAppDataDirectories();

        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    [Fact]
    public void 空根目录_构造时直接拒绝()
    {
        Assert.Throws<ArgumentException>(() => new AppPaths("   "));
    }

    [Fact]
    public void 空的附件目录名_直接拒绝()
    {
        using var local = new TempDirectory();
        var paths = new AppPaths(local.Path);

        Assert.Throws<ArgumentException>(() => paths.SetAttachmentsFolderName("  "));
    }
}
