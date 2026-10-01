using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.Tests.TestDoubles;
using LumiMemo.WinUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.WinUI.Tests.ViewModels;

/// <summary><see cref="ManagerViewModel"/> 的列表排序与搜索（§12.1、§12.2、§15.8）。</summary>
public sealed class ManagerViewModelTests
{
    [Fact]
    public void 空查询_按修改时间倒序()
    {
        using var h = new Harness();
        h.AddNote("# 一", updatedAt: h.Now.AddDays(-9));
        h.AddNote("# 二", updatedAt: h.Now.AddDays(-1));
        h.AddNote("# 三", updatedAt: h.Now.AddDays(-4));
        h.ViewModel.Refresh();

        Assert.Equal(
            new[] { "# 二", "# 三", "# 一" },
            h.ViewModel.Items.Select(item => item.Note.Content).ToArray());
    }

    [Fact]
    public void 查询命中_标题优先于正文_其余不出现()
    {
        using var h = new Harness();
        h.AddNote("# 文档");
        h.AddNote("# 笔记\n提一下文档");
        h.AddNote("# 完全无关");
        h.ViewModel.Refresh();

        h.ViewModel.Query = "文档";

        Assert.Equal(
            new[] { "# 文档", "# 笔记\n提一下文档" },
            h.ViewModel.Items.Select(item => item.Note.Content).ToArray());
    }

    [Fact]
    public void 查询无命中_列表为空_清空查询恢复()
    {
        using var h = new Harness();
        h.AddNote("# 文档");
        h.ViewModel.Refresh();

        h.ViewModel.Query = "找不到的词";
        Assert.Empty(h.ViewModel.Items);

        h.ViewModel.Query = string.Empty;
        Assert.Single(h.ViewModel.Items);
    }

    [Fact]
    public void 置顶便签排在前面()
    {
        using var h = new Harness();
        Note pinned = h.AddNote("# 笔记\n文档在这里放着");
        h.AddNote("# 笔记\n文档在这里放着");
        h.Layouts.GetOrCreate(pinned.Id).IsTopMost = true;

        h.ViewModel.Query = "文档";

        Assert.Equal(pinned.Id, h.ViewModel.Items[0].Note.Id);
    }

    [Fact]
    public async Task 删除便签_列表跟着移除()
    {
        using var h = new Harness();
        Note note = h.AddNote("# 文档");
        Assert.Single(h.ViewModel.Items);

        await h.Windows.DeleteNoteAsync(note);

        Assert.Empty(h.ViewModel.Items);
        Assert.Single(h.Trash.Moved);
    }

    /// <summary>列表窗口的整套替身与 ViewModel（窗口管理器用真对象，替身只到存储/布局层）。</summary>
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

            ViewModel = new ManagerViewModel(Layouts, Clock, Windows);
        }

        public DateTimeOffset Now { get; } = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        public List<Note> Notes { get; } = [];

        public FakeNoteStorage Storage { get; }

        public FakeTrashStore Trash { get; }

        public InMemoryLayoutStore Layouts { get; }

        public FakeClock Clock { get; }

        public NoteWindowManager Windows { get; }

        public ManagerViewModel ViewModel { get; }

        public Note AddNote(string content, DateTimeOffset? updatedAt = null)
        {
            Guid id = Guid.NewGuid();
            var note = new Note
            {
                Id = id,
                FilePath = $@"D:\notes\{id:N}.lumi",
                Content = content,
                CreatedAt = Now.AddYears(-1),
                UpdatedAt = updatedAt ?? Now,
            };
            Layouts.GetOrCreate(id);

            // 走管理器的唯一列表入口——便签集合的真身在 NoteWindowManager 里。
            Windows.RegisterRestoredNote(note);

            return note;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
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
