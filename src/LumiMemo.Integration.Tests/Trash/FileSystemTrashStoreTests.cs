using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Trash;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Trash;

/// <summary>
/// <see cref="FileSystemTrashStore"/> 的集成测试（§7.1–§7.4）。
/// </summary>
/// <remarks>
/// <para>
/// 这一段<strong>只能</strong>用真实文件系统测：它整个存在意义就是「把文件挪到另一个目录」
/// 以及「索引与目录对不上时怎么办」，用内存文件系统替身测出来的只是替身自己的行为。
/// </para>
/// <para>
/// 覆盖重点是 §7.2 那张一致性修复表——索引损坏、目录里有索引里没有、索引里有目录里没有
/// 这三种情况在真实使用中都会被用户手动操作制造出来（自己删 trash 里的文件、
/// 自己把文件拖回笔记目录），而它们各自的处理方式完全不同。
/// </para>
/// </remarks>
public sealed class FileSystemTrashStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid NoteId = Guid.Parse("3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33");

    /// <summary>回收站文件名的时刻格式（§7.1），与存储层保持一致。</summary>
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    // ================= 移入 =================

    [Fact]
    public async Task 移入便签_原文件离开笔记目录并出现在trash里()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "第一版");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        Assert.False(File.Exists(Path.Combine(paths.NotesFolder!, "周报.md")));
        Assert.True(File.Exists(Path.Combine(paths.TrashDirectory, entry.TrashName)));

        // §7.1 的命名规则：{删除时刻}-{原文件名}。
        Assert.Equal("20260101-000000-周报.md", entry.TrashName);
        Assert.Equal("周报.md", entry.OriginalRelativePath);
        Assert.Equal(NoteId, entry.NoteId);
        Assert.Equal(TrashEntryKind.File, entry.Kind);
        Assert.True(entry.Size > 0);
    }

    [Fact]
    public async Task 移入后_索引文件里能查到这一条()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "第一版");

        await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        Assert.True(File.Exists(paths.TrashIndexFile));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(paths.TrashIndexFile));
        JsonElement entry = Assert.Single(
            document.RootElement.GetProperty("entries").EnumerateArray().ToList());

        Assert.Equal("周报.md", entry.GetProperty("originalRelativePath").GetString());

        // kind 必须是小写的 "file"（§7.2 的示例）。写成 "File" 的话，
        // 手改过索引、或者将来换别的解析器时都会对不上。
        Assert.Equal("file", entry.GetProperty("kind").GetString());

        // 时间必须是 §7.2 示例里的带偏移格式，不是空字符串。
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("deletedAt").GetString()));
    }

    [Fact]
    public async Task 同一秒删两个同名文件_第二个名字带序号()
    {
        // 时钟没走动 → 时间戳完全一样。没有序号的话第二个文件会覆盖第一个，
        // 用户删了两份「周报.md」，回收站里只剩一份。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        local.CreateDirectory("a");
        local.CreateDirectory("b");
        WriteNote(paths, "a/周报.md", "甲的周报");
        WriteNote(paths, "b/周报.md", "乙的周报");

        TrashEntry first = await store.MoveFileToTrashAsync("a/周报.md", Guid.NewGuid(), Ct);
        TrashEntry second = await store.MoveFileToTrashAsync("b/周报.md", Guid.NewGuid(), Ct);

        Assert.NotEqual(first.TrashName, second.TrashName);
        Assert.Equal("20260101-000000-周报.md", first.TrashName);
        Assert.Equal("20260101-000000-周报-1.md", second.TrashName);

        // 序号必须在扩展名之前，否则它就不是一个 .md 文件了。
        Assert.EndsWith(".md", second.TrashName, StringComparison.Ordinal);
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task 移入不存在的文件_抛异常()
    {
        using var local = new TempDirectory();
        var (store, _, _) = CreateStore(local);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.MoveFileToTrashAsync("没有这个.md", NoteId, Ct));
    }

    [Fact]
    public async Task 移入逃出笔记目录的路径_直接拒绝()
    {
        // §19.4：回收站只服务笔记目录里的东西。放行的话，一次手改过的
        // 相对路径就能把笔记目录外的文件搬进来。
        using var local = new TempDirectory();
        var (store, _, _) = CreateStore(local);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.MoveFileToTrashAsync(@"..\outside.md", NoteId, Ct));
    }

    [Fact]
    public async Task 整个目录移入_恢复时整棵子树回来()
    {
        // §5.7 的「整个文件夹移到回收站」是整体移动，不是逐个移文件——
        // 逐个移的话往返一趟目录结构就被压平了。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        local.CreateDirectory("项目", "子目录");
        WriteNote(paths, "项目/甲.md", "甲");
        WriteNote(paths, "项目/子目录/乙.md", "乙");

        TrashEntry entry = await store.MoveDirectoryToTrashAsync("项目", Ct);

        Assert.Equal(TrashEntryKind.Directory, entry.Kind);
        Assert.Null(entry.NoteId);
        Assert.False(Directory.Exists(Path.Combine(paths.NotesFolder!, "项目")));
        Assert.True(entry.Size > 0);

        await store.RestoreAsync(entry, null, Ct);

        Assert.True(File.Exists(Path.Combine(paths.NotesFolder!, "项目", "子目录", "乙.md")));
    }

    // ================= §7.2 的一致性修复 =================

    [Fact]
    public async Task 索引不存在_列出时凭目录内容重建()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        // 模拟「用户手动把一个文件扔进 trash」，此时索引里什么都没有。
        WriteTrashFile(paths, "20260315-143000-会议记录.md", "内容");

        IReadOnlyList<TrashEntry> entries = await store.ListAsync(Ct);

        TrashEntry entry = Assert.Single(entries);

        // 时刻与原文件名都从文件名里解析出来（§7.2 第 1 行）。
        // 比的是「这个时刻能不能再写回同一个名字」，而不是某个绝对 UTC 值——
        // 文件名里的时刻是按本地时间解读的，比绝对值的断言会随跑测试的机器时区变红。
        Assert.Equal("会议记录.md", entry.OriginalRelativePath);
        Assert.Equal("20260315-143000", entry.DeletedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        // 重建出来的结果要落盘。
        Assert.True(File.Exists(paths.TrashIndexFile));
    }

    [Fact]
    public async Task 索引不存在_从文件里的FrontMatter补出便签id()
    {
        // 手动扔进来的文件恰好是一张便签时，它的身份就在文件头部。
        // 不读出来的话，「按 id 恢复」这条路对它永远找不到条目。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        WriteTrashFile(
            paths,
            "20260315-143000-会议记录.md",
            $"---\nid: {NoteId:D}\ncolor: yellow\n---\n\n下周的会议\n");

        TrashEntry entry = Assert.Single(await store.ListAsync(Ct));

        Assert.Equal(NoteId, entry.NoteId);
    }

    [Fact]
    public async Task 索引损坏_按目录内容重建而不是报错()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteTrashFile(paths, "20260315-143000-会议记录.md", "内容");

        Directory.CreateDirectory(Path.GetDirectoryName(paths.TrashIndexFile)!);
        File.WriteAllText(paths.TrashIndexFile, "{ 这不是 JSON", Encoding.UTF8);

        TrashEntry entry = Assert.Single(await store.ListAsync(Ct));

        Assert.Equal("会议记录.md", entry.OriginalRelativePath);
    }

    [Fact]
    public async Task 索引损坏_不留下备份文件()
    {
        // 与 settings.json / layout.json 不同，回收站索引是派生数据——
        // 目录里有全部信息，留一个 .corrupt-* 只是往用户的笔记目录里塞垃圾。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        Directory.CreateDirectory(Path.GetDirectoryName(paths.TrashIndexFile)!);
        File.WriteAllText(paths.TrashIndexFile, "彻底坏掉", Encoding.UTF8);

        _ = await store.ListAsync(Ct);

        string[] leftovers = Directory.GetFiles(
            Path.GetDirectoryName(paths.TrashIndexFile)!,
            "*.corrupt-*");

        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task 索引里有但文件不在_原路径空着时保留并标已不在()
    {
        // 「文件已不在」是真信息：用户可能只是把 trash 里的文件挪到别处去了，
        // 条目本身还是该看得见的线索（§7.2）。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry moved = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        // 用户自己把 trash 里的文件删了。
        File.Delete(Path.Combine(paths.TrashDirectory, moved.TrashName));

        TrashEntry entry = Assert.Single(await store.ListAsync(Ct));

        Assert.True(entry.IsFileMissing);
        Assert.Equal("周报.md", entry.OriginalRelativePath);
    }

    [Fact]
    public async Task 索引里有但文件不在_原路径已被占用时静默移除条目()
    {
        // 用户把文件从 trash 里拖回了原位。再留着条目的话，
        // 他会在回收站里看到一张此刻正开着的便签。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry moved = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);
        File.Delete(Path.Combine(paths.TrashDirectory, moved.TrashName));

        // 手动放回原位。
        WriteNote(paths, "周报.md", "内容");

        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task 一切正常时_列出一遍不重写索引文件()
    {
        // 一次「打开回收站看看」不该改 trash-index.json 的修改时间——
        // 那会干扰用户自己的备份与同步工具，也让排查时的线索失真。
        using var local = new TempDirectory();
        var (store, paths, clock) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        byte[] before = File.ReadAllBytes(paths.TrashIndexFile);
        DateTime writtenAt = File.GetLastWriteTimeUtc(paths.TrashIndexFile);

        // 把时钟往前拨：索引的时间戳就是从这个时钟来的（AtomicFileWriter 会把
        // 文件的修改时间设成它）。于是「有没有重写过」这件事在修改时间上一眼可辨。
        clock.Advance(TimeSpan.FromHours(1));

        _ = await store.ListAsync(Ct);
        _ = await store.ListAsync(Ct);

        Assert.Equal(before, File.ReadAllBytes(paths.TrashIndexFile));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(paths.TrashIndexFile));
    }

    [Fact]
    public async Task 索引里有但文件不在_这一条本身不触发重写索引()
    {
        // IsFileMissing 是运行时算出来的，不落盘。为它重写一次内容完全相同的
        // 索引文件没有意义——而如果它触发了重写，这个用例会看到时间戳变了。
        using var local = new TempDirectory();
        var (store, paths, clock) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry moved = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);
        File.Delete(Path.Combine(paths.TrashDirectory, moved.TrashName));

        DateTime writtenAt = File.GetLastWriteTimeUtc(paths.TrashIndexFile);
        clock.Advance(TimeSpan.FromHours(1));

        TrashEntry entry = Assert.Single(await store.ListAsync(Ct));

        Assert.True(entry.IsFileMissing);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(paths.TrashIndexFile));
    }

    [Fact]
    public async Task 没有任何条目时_列出不创建trash目录也不创建索引()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);

        Assert.Empty(await store.ListAsync(Ct));

        Assert.False(Directory.Exists(paths.TrashDirectory));
        Assert.False(File.Exists(paths.TrashIndexFile));
    }

    // ================= 恢复 =================

    [Fact]
    public async Task 恢复_回到原位并离开trash()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        string restored = await store.RestoreAsync(entry, null, Ct);

        Assert.Equal("周报.md", restored);
        Assert.True(File.Exists(Path.Combine(paths.NotesFolder!, "周报.md")));
        Assert.False(File.Exists(Path.Combine(paths.TrashDirectory, entry.TrashName)));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task 恢复_回到原来的子目录里()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        local.CreateDirectory("归档");
        WriteNote(paths, "归档/周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("归档/周报.md", NoteId, Ct);

        string restored = await store.RestoreAsync(entry, null, Ct);

        Assert.Equal(Path.Combine("归档", "周报.md"), restored);
        Assert.True(File.Exists(Path.Combine(paths.NotesFolder!, "归档", "周报.md")));
    }

    [Fact]
    public async Task 原位置被占用_恢复时加序号而不是覆盖()
    {
        // §7.3。覆盖是不可逆的，而用户的意图从来不是「毁掉现在占着这个位置的那张便签」。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "要删的");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        // 删掉之后用户又新建了一张同名的。
        WriteNote(paths, "周报.md", "新写的");

        string restored = await store.RestoreAsync(entry, null, Ct);

        Assert.Equal("周报 (1).md", restored);
        Assert.Contains(
            "新写的",
            File.ReadAllText(Path.Combine(paths.NotesFolder!, "周报.md"), Encoding.UTF8),
            StringComparison.Ordinal);
        Assert.Contains(
            "要删的",
            File.ReadAllText(Path.Combine(paths.NotesFolder!, "周报 (1).md"), Encoding.UTF8),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 原位置被占用_连撞两次时序号往后走()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "要删的");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        WriteNote(paths, "周报.md", "甲");
        WriteNote(paths, "周报 (1).md", "乙");

        string restored = await store.RestoreAsync(entry, null, Ct);

        Assert.Equal("周报 (2).md", restored);
    }

    [Fact]
    public async Task 恢复到笔记目录根_落在根上而不是原位()
    {
        // §7.3 的第二档：用户选了「恢复到笔记目录根」。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        local.CreateDirectory("归档");
        WriteNote(paths, "归档/周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("归档/周报.md", NoteId, Ct);

        string restored = await store.RestoreAsync(entry, "周报.md", Ct);

        Assert.Equal("周报.md", restored);
        Assert.True(File.Exists(Path.Combine(paths.NotesFolder!, "周报.md")));

        // 原来的子目录还在（移文件不会顺手删空目录），但那个便签已经不在里面了。
        Assert.False(File.Exists(Path.Combine(paths.NotesFolder!, "归档", "周报.md")));
    }

    [Fact]
    public async Task 恢复到笔记目录之外_直接拒绝()
    {
        // §19.4。恢复是「往用户的磁盘上放东西」，越界的后果比移入更严重。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RestoreAsync(entry, @"..\跑出去了.md", Ct));

        // 拒绝之后文件必须还在 trash 里，不能被挪到一半。
        Assert.True(File.Exists(Path.Combine(paths.TrashDirectory, entry.TrashName)));
    }

    [Fact]
    public async Task 恢复一个文件已不在的条目_抛异常()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);
        File.Delete(Path.Combine(paths.TrashDirectory, entry.TrashName));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.RestoreAsync(entry, null, Ct));
    }

    [Fact]
    public async Task 原位置是否被占用_不碰磁盘就能答上来()
    {
        // 它存在的意义就是让上层在动手之前问用户（§7.3）。这里若会写盘、
        // 会抛异常，「取消」这个选项就没意义了。
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "周报.md", "内容");

        TrashEntry entry = await store.MoveFileToTrashAsync("周报.md", NoteId, Ct);

        Assert.False(store.IsOriginalPathOccupied(entry));

        WriteNote(paths, "周报.md", "新写的");

        Assert.True(store.IsOriginalPathOccupied(entry));
    }

    // ================= 清理 =================

    [Fact]
    public async Task 清空_删掉文件并清空索引()
    {
        using var local = new TempDirectory();
        var (store, paths, _) = CreateStore(local);
        WriteNote(paths, "甲.md", "甲");
        WriteNote(paths, "乙.md", "乙");

        await store.MoveFileToTrashAsync("甲.md", Guid.NewGuid(), Ct);
        await store.MoveFileToTrashAsync("乙.md", Guid.NewGuid(), Ct);

        await store.EmptyAsync(Ct);

        Assert.Empty(await store.ListAsync(Ct));
        Assert.Empty(Directory.GetFiles(paths.TrashDirectory));
    }

    [Fact]
    public async Task 保留期已过_清掉那一条()
    {
        using var local = new TempDirectory();
        var (store, paths, clock) = CreateStore(local);
        WriteNote(paths, "老.md", "老内容");

        await store.MoveFileToTrashAsync("老.md", Guid.NewGuid(), Ct);

        clock.Advance(TimeSpan.FromDays(31));

        int purged = await store.PurgeExpiredAsync(30, Ct);

        Assert.Equal(1, purged);
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task 保留期未到_一条都不动()
    {
        using var local = new TempDirectory();
        var (store, paths, clock) = CreateStore(local);
        WriteNote(paths, "新.md", "新内容");

        await store.MoveFileToTrashAsync("新.md", Guid.NewGuid(), Ct);

        clock.Advance(TimeSpan.FromDays(29));

        Assert.Equal(0, await store.PurgeExpiredAsync(30, Ct));
        Assert.Single(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task 保留期传零_抛异常而不是删光()
    {
        // 「0 = 永不清理」是策略，判断在 TrashService 里。传 0 进来的人想要的
        // 显然是「全部删掉」，而那不该由一次笔误实现。
        using var local = new TempDirectory();
        var (store, _, _) = CreateStore(local);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.PurgeExpiredAsync(0, Ct));
    }

    // ---- 辅助 ----

    private static (FileSystemTrashStore Store, AppPaths Paths, FakeClock Clock) CreateStore(TempDirectory local)
    {
        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(local.CreateDirectory("notes"));

        var clock = new FakeClock();

        var store = new FileSystemTrashStore(
            paths,
            clock,
            new AtomicFileWriter(clock, retryDelaysMilliseconds: []),
            NullLogger<FileSystemTrashStore>.Instance);

        return (store, paths, clock);
    }

    /// <summary>在笔记目录里写一个便签文件（含最小 Front Matter）。</summary>
    private static void WriteNote(AppPaths paths, string relativePath, string content)
    {
        string full = Path.Combine(paths.NotesFolder!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        File.WriteAllText(
            full,
            $"---\nid: {Guid.NewGuid():D}\n---\n\n{content}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>直接往 <c>trash/</c> 里放一个文件，模拟用户手动操作。</summary>
    private static void WriteTrashFile(AppPaths paths, string trashName, string content)
    {
        Directory.CreateDirectory(paths.TrashDirectory);
        File.WriteAllText(Path.Combine(paths.TrashDirectory, trashName), content, Encoding.UTF8);
    }
}
