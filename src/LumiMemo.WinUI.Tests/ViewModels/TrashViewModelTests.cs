using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.Tests.TestDoubles;
using LumiMemo.WinUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.WinUI.Tests.ViewModels;

/// <summary><see cref="TrashViewModel"/> 的列表、恢复与清空编排。</summary>
public sealed class TrashViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task 刷新_把替身返回的条目摆进列表()
    {
        using var h = new Harness();
        h.Trash.EntriesToReturn.Add(h.NewEntry("甲", h.Now));
        h.Trash.EntriesToReturn.Add(h.NewEntry("乙", h.Now.AddMinutes(-5)));

        await h.ViewModel.RefreshAsync(Ct);

        Assert.Equal(
            new[] { "甲", "乙" },
            h.ViewModel.Items.Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task 恢复_读回便笺并登记进便签列表()
    {
        using var h = new Harness();
        Note note = h.NewNote("恢复我");
        h.Trash.EntriesToReturn.Add(h.NewEntry("恢复我", h.Now, note.Id));
        h.Trash.RestorePaths[note.Id] = note.FilePath;
        h.Storage.NotesToLoad.Add(note);
        await h.ViewModel.RefreshAsync(Ct);

        await h.ViewModel.RestoreAsync(h.ViewModel.Items[0], Ct);

        Assert.Contains(note, h.Windows.Notes);
    }

    [Fact]
    public async Task 恢复失败_异常原样上抛给窗口()
    {
        using var h = new Harness();
        h.Trash.EntriesToReturn.Add(h.NewEntry("找不到了", h.Now));
        h.Trash.RestoreException = new InvalidOperationException("回收站里已经找不到这张便笺了。");
        await h.ViewModel.RefreshAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.ViewModel.RestoreAsync(h.ViewModel.Items[0], Ct));
    }

    [Fact]
    public async Task 彻底删除_记录到替身并刷新()
    {
        using var h = new Harness();
        h.Trash.EntriesToReturn.Add(h.NewEntry("删我", h.Now));
        await h.ViewModel.RefreshAsync(Ct);

        await h.ViewModel.PurgeAsync(h.ViewModel.Items[0], Ct);

        Assert.Single(h.Trash.Purged);
    }

    [Fact]
    public async Task 清空_逐条彻底删除()
    {
        using var h = new Harness();
        h.Trash.EntriesToReturn.Add(h.NewEntry("甲", h.Now));
        h.Trash.EntriesToReturn.Add(h.NewEntry("乙", h.Now.AddMinutes(-1)));
        await h.ViewModel.RefreshAsync(Ct);

        await h.ViewModel.PurgeAllAsync(Ct);

        Assert.Equal(2, h.Trash.Purged.Count);
    }

    private sealed class Harness : IDisposable
    {
        private readonly NoteTitleCoordinator _titles;
        private readonly AutoSaveService _autoSave;

        public Harness()
        {
            Clock = new FakeClock(Now);
            Storage = new FakeNoteStorage();
            Trash = new FakeTrashStore();
            Layouts = new InMemoryLayoutStore();

            _titles = new NoteTitleCoordinator(
                Notes, Storage, Clock, new AppSettings { Llm = new LlmSettings() }, new NeverGenerator());
            _autoSave = new AutoSaveService(
                new ManualUiTimerFactory(), NullLogger<AutoSaveService>.Instance);

            var factory = new NoteWindowFactory(
                Storage, Clock, Layouts, _autoSave, _titles, NullLoggerFactory.Instance);
            Windows = new NoteWindowManager(Notes, Storage, Trash, Layouts, _titles, factory);

            ViewModel = new TrashViewModel(
                Trash, Storage, Windows, NullLogger<TrashViewModel>.Instance);
        }

        public DateTimeOffset Now { get; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public List<Note> Notes { get; } = [];

        public FakeNoteStorage Storage { get; }

        public FakeTrashStore Trash { get; }

        public InMemoryLayoutStore Layouts { get; }

        public FakeClock Clock { get; }

        public NoteWindowManager Windows { get; }

        public TrashViewModel ViewModel { get; }

        public Note NewNote(string content)
        {
            Guid id = Guid.NewGuid();
            var note = new Note
            {
                Id = id,
                FilePath = $@"D:\notes\{id:N}.lumi",
                Content = content,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            Layouts.GetOrCreate(id);

            return note;
        }

        public TrashEntry NewEntry(string title, DateTimeOffset deletedAt, Guid? id = null) =>
            new() { NoteId = id ?? Guid.NewGuid(), Title = title, DeletedAt = deletedAt };

        public void Dispose()
        {
            _titles.Dispose();
            _autoSave.Dispose();
        }
    }

    private sealed class NeverGenerator : ITitleGenerator
    {
        public Task<string> GenerateAsync(string content, LlmSettings settings, CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidOperationException("测试里 LLM 是关的，不该走到这里。"));
    }
}
