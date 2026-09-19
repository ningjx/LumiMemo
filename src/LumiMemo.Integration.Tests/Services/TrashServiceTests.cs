using System.IO;
using System.Text;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Trash;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Services;

/// <summary>
/// <see cref="TrashService"/> 的集成测试：删除与恢复对<strong>磁盘 + 内存</strong>的双向影响。
/// </summary>
/// <remarks>
/// <para>
/// 这里用的是真实的 <see cref="MarkdownNoteRepository"/> 与
/// <see cref="FileSystemTrashStore"/>，因为它们各自已被自己的测试钉住，
/// 而本类要验的是<strong>它们接起来之后</strong>才存在的问题：
/// 文件确实挪走了、但内存里那张便签有没有跟着走；文件确实回来了、
/// 但 <c>NoteStore</c> 里拿到的是不是一个内容正确、路径正确、id 不撞车的新实例。
/// </para>
/// <para>
/// 内存侧的 <c>NoteService</c> 转发已在 Core.Tests 里用替身覆盖，此处不重复。
/// </para>
/// </remarks>
public sealed class TrashServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================= 删除 =================

    [Fact]
    public async Task 删除便签_文件挪进回收站且内存与索引都不再留着它()
    {
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "第一版");
        Note note = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(note.Id, Ct);

        // 磁盘：原文件离开笔记目录，出现在 trash 里。§7.1 说的「只移动，不删除」。
        Assert.False(File.Exists(Path.Combine(h.Paths.NotesFolder!, "周报.md")));
        Assert.True(File.Exists(Path.Combine(h.Paths.TrashDirectory, entry.TrashName)));
        Assert.Equal("周报.md", entry.OriginalRelativePath);
        Assert.Equal(note.Id, entry.NoteId);

        // 内存：不摘的话管理器列表里还留着一条，用户点开就会看到自己刚删掉的便签。
        Assert.Equal(0, h.Notes.Count);
        Assert.Null(h.Notes.TryGet(note.Id));
        Assert.Empty(h.Index.GetPlainText(note.Id));
    }

    [Fact]
    public async Task 删除便签_便签不在内存里时抛异常()
    {
        // 调用方拿着的 id 可能已经过期（回收站被清空、目录被换掉）。
        // 静默返回会让界面以为「删掉了」而回收站里什么都没有。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.MoveNoteToTrashAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task 删除便签_文件在笔记目录外时拒绝移入()
    {
        // 用户改过 settings.json，或者文件是被别处移进来的。把笔记目录外的文件
        // 搬进笔记目录是越界动作，宁可当场失败。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);

        string outside = local.Combine("outside.md");
        File.WriteAllText(outside, "不在笔记目录里", Encoding.UTF8);

        Note note = NewNote(Guid.NewGuid(), outside);
        h.Notes.Add(note);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.MoveNoteToTrashAsync(note.Id, Ct));

        Assert.True(File.Exists(outside));
    }

    // ================= 恢复 =================

    [Fact]
    public async Task 恢复便签_文件回到原位并把内容读回内存()
    {
        // 这一条是 «恢复走的不是整目录重扫»（§18.4）的落地证明：恢复之后
        // NoteStore 里必须重新出现一张 id 相同的便签，而不是空着等下次重启。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "第一版");
        Note original = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(original.Id, Ct);
        string restored = await h.Service.RestoreAsync(entry, ct: Ct);

        Assert.Equal("周报.md", restored);
        Assert.True(File.Exists(Path.Combine(h.Paths.NotesFolder!, "周报.md")));
        Assert.False(File.Exists(Path.Combine(h.Paths.TrashDirectory, entry.TrashName)));

        Note back = Assert.Single(h.Notes.Snapshot());
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(Path.Combine(h.Paths.NotesFolder!, "周报.md"), back.FilePath);
        Assert.Contains("第一版", back.Content, StringComparison.Ordinal);

        // 索引也得跟着回来，否则管理器里搜不到这张刚恢复的便签。
        Assert.Contains("第一版", h.Index.GetPlainText(back.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 恢复便签_恢复到笔记目录根时也回内存并且落在根上()
    {
        // §7.3 的第二档：路径是调用方给的，与条目记的原位置无关。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "归档/周报.md", "归档里的周报");
        Note original = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(original.Id, Ct);
        string restored = await h.Service.RestoreAsync(entry, TrashService.RootTargetFor(entry), Ct);

        Assert.Equal("周报.md", restored);
        Assert.True(File.Exists(Path.Combine(h.Paths.NotesFolder!, "周报.md")));

        Note back = Assert.Single(h.Notes.Snapshot());
        Assert.Equal(Path.Combine(h.Paths.NotesFolder!, "周报.md"), back.FilePath);
    }

    [Fact]
    public async Task 恢复目录级条目_把整棵子树的便签都读回内存()
    {
        // §5.7 的「整个文件夹移到回收站」。目录级条目一次带回多个文件，
        // 而恢复之后它们必须逐个进内存——不重扫整个笔记目录。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "归档/甲.md", "甲的内容");
        WriteNote(h.Paths, "归档/乙.md", "乙的内容");
        WriteNote(h.Paths, "归档/子/丙.md", "丙的内容");

        TrashEntry entry = await h.Service.MoveDirectoryToTrashAsync("归档", Ct);
        Assert.Equal(TrashEntryKind.Directory, entry.Kind);
        Assert.Equal(0, h.Notes.Count);

        string restored = await h.Service.RestoreAsync(entry, ct: Ct);

        Assert.Equal("归档", restored);
        Assert.Equal(3, h.Notes.Count);
        Assert.Contains(h.Notes.Snapshot(), n => n.Content.Contains("甲的内容", StringComparison.Ordinal));
        Assert.Contains(h.Notes.Snapshot(), n => n.Content.Contains("乙的内容", StringComparison.Ordinal));

        // 子目录里的那张也要带回来——递归枚举漏掉一层的话，用户会以为便签丢了。
        Assert.Contains(h.Notes.Snapshot(), n => n.Content.Contains("丙的内容", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 恢复时id与内存里的便签撞车_重新分配id并写回文件()
    {
        // §7.3：用户删掉一张便签之后又新建了一张，恰好复制了同一个 id
        // （手工复制 .md、或者从备份里拖回来）。恢复的那张必须让路——
        // 否则 Store 里会被静默覆盖，用户点一次「恢复」，丢掉的是另一张便签。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);

        Guid shared = Guid.Parse("11111111-2222-3333-4444-555555555555");
        WriteNote(h.Paths, "旧.md", "旧的那张", shared);
        Note old = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(old.Id, Ct);

        // 现在新建一张用了同一个 id 的便签。
        WriteNote(h.Paths, "新.md", "新的那张", shared);
        Note fresh = Assert.Single(await LoadIntoMemoryAsync(h));
        Assert.Equal(shared, fresh.Id);

        await h.Service.RestoreAsync(entry, ct: Ct);

        Assert.Equal(2, h.Notes.Count);

        string restoredPath = Path.Combine(h.Paths.NotesFolder!, "旧.md");
        Note restored = Assert.Single(h.Notes.Snapshot(), n => n.FilePath == restoredPath);
        Assert.NotEqual(shared, restored.Id);

        // 换 id 必须落到磁盘上。只改内存的话，下次启动扫描又会读到那个重复的 id，
        // 于是每次启动都重演一次冲突。
        ParsedNoteFile parsed = FrontMatterParser.Parse(File.ReadAllBytes(restoredPath));
        Assert.Equal(restored.Id, parsed.Result.Id);

        // 内容与新建的那张都还在——没有谁被覆盖。
        Assert.Contains("旧的那张", restored.Content, StringComparison.Ordinal);
        Assert.Contains("新的那张", h.Notes.TryGet(shared)!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 恢复便签_原位置被占用时给出冲突提示并让开路径()
    {
        // §7.3 的第一档撞名处理：上层要先问用户，问之前得先知道「会不会撞」。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "回收站里的周报");
        Note note = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(note.Id, Ct);
        Assert.False(h.Service.NeedsConflictResolution(entry));

        // 用户在这期间又在原位写了一个同名文件。
        WriteNote(h.Paths, "周报.md", "新写的周报");
        Assert.True(h.Service.NeedsConflictResolution(entry));

        string restored = await h.Service.RestoreAsync(entry, ct: Ct);

        // 让路而不是覆盖：新写的那个文件必须原封不动。
        Assert.Equal("周报 (1).md", restored);
        Assert.Contains(
            "新写的周报",
            File.ReadAllText(Path.Combine(h.Paths.NotesFolder!, "周报.md")),
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Paths.NotesFolder!, "周报 (1).md")));
    }

    [Fact]
    public async Task 恢复便签_按id找条目_清空之后就找不到了()
    {
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "第一版");
        Note note = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(note.Id, Ct);

        TrashEntry? found = await h.Service.FindByNoteIdAsync(note.Id, Ct);
        Assert.NotNull(found);
        Assert.Equal(entry.TrashName, found.TrashName);

        await h.Service.EmptyAsync(Ct);

        Assert.Null(await h.Service.FindByNoteIdAsync(note.Id, Ct));
        Assert.Empty(await h.Service.ListAsync(Ct));
    }

    [Fact]
    public async Task 恢复便签_回收站里没有这个id时找不到了()
    {
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);

        Assert.Null(await h.Service.FindByNoteIdAsync(Guid.NewGuid(), Ct));
    }

    // ================= 清理 =================

    [Fact]
    public async Task 保留期为零_一条都不清()
    {
        // §7.4：「0 = 永不清理」是策略，判断在 TrashService 里——
        // 存储层收到 0 会直接抛异常（它接到的 0 只可能是笔误）。
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "第一版");
        Note note = Assert.Single(await LoadIntoMemoryAsync(h));

        await h.Service.MoveNoteToTrashAsync(note.Id, Ct);

        h.Clock.Advance(TimeSpan.FromDays(3650));
        h.Service.RetentionDays = 0;

        Assert.Equal(0, await h.Service.PurgeExpiredAsync(Ct));
        Assert.Single(await h.Service.ListAsync(Ct));
    }

    [Fact]
    public async Task 保留期到期_条目被清掉()
    {
        using var local = new TempDirectory();
        Harness h = CreateHarness(local);
        WriteNote(h.Paths, "周报.md", "第一版");
        Note note = Assert.Single(await LoadIntoMemoryAsync(h));

        TrashEntry entry = await h.Service.MoveNoteToTrashAsync(note.Id, Ct);

        h.Service.RetentionDays = 7;
        h.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, await h.Service.PurgeExpiredAsync(Ct));

        h.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await h.Service.PurgeExpiredAsync(Ct));
        Assert.Empty(await h.Service.ListAsync(Ct));

        // 文件也真的没了——只从索引里摘掉的话，trash 目录会越积越大。
        Assert.False(File.Exists(Path.Combine(h.Paths.TrashDirectory, entry.TrashName)));
    }

    // ================= 纯函数 =================

    [Fact]
    public void 根目录目标_只取文件名()
    {
        Assert.Equal("周报.md", TrashService.RootTargetFor(Entry(@"归档\子\周报.md")));
        Assert.Equal("归档", TrashService.RootTargetFor(Entry(@"备份\归档")));
    }

    [Fact]
    public void 根目录目标_带点的路径也逃不出笔记目录()
    {
        // 索引被手改过的话，OriginalRelativePath 里可能带着 ..。
        // 只取最后一段的文件名，这条路径就不可能借「恢复到根」跑到笔记目录外面去（§19.4）。
        Assert.Equal("evil.md", TrashService.RootTargetFor(Entry(@"..\..\evil.md")));
        Assert.Equal("evil.md", TrashService.RootTargetFor(Entry(@"C:\Windows\evil.md")));
    }

    // ---- 辅助 ----

    private static TrashEntry Entry(string originalRelativePath) => new()
    {
        TrashName = "20260101-000000-x.md",
        OriginalRelativePath = originalRelativePath,
        DeletedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Kind = TrashEntryKind.File,
    };

    private static Harness CreateHarness(TempDirectory local)
    {
        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(local.CreateDirectory("notes"));

        var clock = new FakeClock();
        var writer = new AtomicFileWriter(clock, retryDelaysMilliseconds: []);

        // 重试间隔清空是刻意的：文件被独占在这里是确定性复现的，
        // 留着退避重试只会让每条用例多花几百毫秒。
        var repository = new MarkdownNoteRepository(
            paths,
            clock,
            writer,
            NullLogger<MarkdownNoteRepository>.Instance,
            lockRetryDelaysMilliseconds: []);

        var trash = new FileSystemTrashStore(
            paths,
            clock,
            writer,
            NullLogger<FileSystemTrashStore>.Instance);

        var notes = new NoteStore();
        var index = new SearchIndex();
        var service = new TrashService(trash, paths, notes, index, repository);

        return new Harness(service, paths, notes, index, repository, clock);
    }

    /// <summary>走一次真实的启动扫描，把磁盘上的便签搬进内存与索引。</summary>
    private static async Task<IReadOnlyList<Note>> LoadIntoMemoryAsync(Harness h)
    {
        IReadOnlyList<Note> loaded = await h.Repository.LoadAllAsync(Ct);

        foreach (Note note in loaded)
        {
            h.Notes.Add(note);
            h.Index.OnNoteAdded(note);
        }

        return loaded;
    }

    /// <summary>在笔记目录里写一个便签文件（含最小 Front Matter）。</summary>
    private static void WriteNote(AppPaths paths, string relativePath, string content, Guid? id = null)
    {
        string full = Path.Combine(paths.NotesFolder!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        File.WriteAllText(
            full,
            $"---\nid: {id ?? Guid.NewGuid():D}\n---\n\n{content}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Note NewNote(Guid id, string filePath) => new()
    {
        Id = id,
        FilePath = filePath,
        Content = "内容",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private sealed record Harness(
        TrashService Service,
        AppPaths Paths,
        NoteStore Notes,
        SearchIndex Index,
        MarkdownNoteRepository Repository,
        FakeClock Clock);
}
