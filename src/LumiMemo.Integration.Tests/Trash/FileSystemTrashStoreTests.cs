using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Trash;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Trash;

/// <summary>
/// <see cref="FileSystemTrashStore"/> 的集成测试：移入、恢复、彻底删除、过期清理，
/// 以及「索引坏了文件不丢」这条底线。
/// </summary>
public sealed class FileSystemTrashStoreTests
{
    private static readonly DateTimeOffset When =
        new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 移入回收站_文件离开笔记目录并列进回收站()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("要被删的便笺");

        await h.Trash.MoveToTrashAsync(note, Ct);

        Assert.False(File.Exists(note.FilePath));

        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));
        Assert.Equal(note.Id, entry.NoteId);
        Assert.Equal(note.Title, entry.Title);
        Assert.Equal(h.Clock.Now, entry.DeletedAt);
    }

    [Fact]
    public async Task 恢复_文件回笔记目录且正文完好()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("恢复我");
        await h.Trash.MoveToTrashAsync(note, Ct);
        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));

        string restoredPath = await h.Trash.RestoreAsync(entry, Ct);

        Assert.True(File.Exists(restoredPath));
        Note? restored = await h.Storage.TryLoadAsync(restoredPath, Ct);
        Assert.NotNull(restored);
        Assert.Equal("恢复我", restored.Content);
        Assert.Empty(await h.Trash.ListAsync(Ct));
    }

    [Fact]
    public async Task 彻底删除_文件消失且列表为空()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("删干净");
        await h.Trash.MoveToTrashAsync(note, Ct);
        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));

        await h.Trash.PurgeAsync(entry, Ct);

        Assert.Empty(await h.Trash.ListAsync(Ct));
    }

    [Fact]
    public async Task 过期清理_只清超期的_保留期内不动()
    {
        using var h = new Harness();
        Note old = await h.CreateNoteAsync("旧便笺");
        await h.Trash.MoveToTrashAsync(old, Ct);

        h.Clock.Advance(TimeSpan.FromDays(10));
        Note fresh = await h.CreateNoteAsync("新便笺");
        await h.Trash.MoveToTrashAsync(fresh, Ct);

        h.Clock.Advance(TimeSpan.FromDays(25));   // 旧：35 天；新：25 天
        await h.Trash.PurgeExpiredAsync(TimeSpan.FromDays(30), Ct);

        TrashEntry remaining = Assert.Single(await h.Trash.ListAsync(Ct));
        Assert.Equal(fresh.Id, remaining.NoteId);
    }

    [Fact]
    public async Task 索引损坏_文件仍在回收站里_标题降级显示()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("索引会坏");
        await h.Trash.MoveToTrashAsync(note, Ct);

        File.WriteAllText(h.IndexPath, "{ 这不是 JSON");

        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));
        Assert.Equal(note.Id, entry.NoteId);
        Assert.Equal("（未知标题）", entry.Title);
    }

    [Fact]
    public async Task 恢复时目标已存在_报错且两边文件都不动()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("两边都有");
        await h.Trash.MoveToTrashAsync(note, Ct);
        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));

        // 笔记目录里凭空又出现一张同 id 的文件（外部手放或同步工具的场面）。
        File.WriteAllText(note.FilePath, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Trash.RestoreAsync(entry, Ct));
        Assert.True(File.Exists(note.FilePath));
        Assert.Single(await h.Trash.ListAsync(Ct));   // 回收站那份也还在
    }

    [Fact]
    public async Task 恢复时回收站文件已不在_报错并清掉孤儿条目()
    {
        using var h = new Harness();
        Note note = await h.CreateNoteAsync("已被外部清掉");
        await h.Trash.MoveToTrashAsync(note, Ct);
        TrashEntry entry = Assert.Single(await h.Trash.ListAsync(Ct));

        File.Delete(Path.Combine(h.TrashFolder, $"{note.Id:N}.lumi"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Trash.RestoreAsync(entry, Ct));
        Assert.Empty(await h.Trash.ListAsync(Ct));
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Clock = new FakeClock(When);
            Storage = new LumiNoteStorage(
                Temp.Path, Clock, new AtomicFileWriter(Clock), NoteColor.Yellow,
                NullLogger<LumiNoteStorage>.Instance);
            Trash = new FileSystemTrashStore(
                Temp.Path, Clock, new AtomicFileWriter(Clock),
                NullLogger<FileSystemTrashStore>.Instance);
        }

        public TempDirectory Temp { get; } = new();

        public FakeClock Clock { get; }

        public LumiNoteStorage Storage { get; }

        public FileSystemTrashStore Trash { get; }

        public string IndexPath =>
            Path.Combine(Temp.Path, FileSystemTrashStore.MetadataDirectoryName, "trash-index.json");

        public string TrashFolder =>
            Path.Combine(Temp.Path, FileSystemTrashStore.MetadataDirectoryName, "trash");

        public async Task<Note> CreateNoteAsync(string content)
        {
            Note note = await Storage.CreateAsync(ct: Ct);
            note.Content = content;
            await Storage.SaveAsync(note, Ct);

            return note;
        }

        public void Dispose() => Temp.Dispose();
    }
}
