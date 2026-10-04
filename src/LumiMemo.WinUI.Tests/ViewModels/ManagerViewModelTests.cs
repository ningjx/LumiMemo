using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
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

    [Fact]
    public void 多关键词_缺一个词就不出现()
    {
        using var h = new Harness();
        h.AddNote("# 文档\n会议记录写完了");
        h.AddNote("# 文档\n别的内容");

        h.ViewModel.Query = "文档 会议";

        Assert.Single(h.ViewModel.Items);
    }

    [Fact]
    public void 颜色筛选_只留该颜色()
    {
        using var h = new Harness();
        Note blue = h.AddNote("# 蓝签");
        h.AddNote("# 黄签");
        blue.Color = NoteColor.Blue;

        h.ViewModel.ColorFilter = NoteColor.Blue;

        Assert.Equal(blue.Id, Assert.Single(h.ViewModel.Items).Note.Id);
    }

    [Fact]
    public void 排序切换_把结果改成按修改时间()
    {
        using var h = new Harness();
        Note oldTitleHit = h.AddNote("# 文档", updatedAt: h.Now.AddDays(-20));
        Note newBodyHit = h.AddNote("# 笔记\n文档在这里", updatedAt: h.Now.AddDays(-1));

        h.ViewModel.Query = "文档";

        // 相关度：标题命中的（虽然是旧的）在前。
        Assert.Equal(oldTitleHit.Id, h.ViewModel.Items[0].Note.Id);

        h.ViewModel.SortByModifiedTime = true;

        // 修改时间：新的在前。
        Assert.Equal(newBodyHit.Id, h.ViewModel.Items[0].Note.Id);
    }

    [Fact]
    public void 排序切换_再点一次回到默认顺序()
    {
        using var h = new Harness();
        Note oldTitleHit = h.AddNote("# 文档", updatedAt: h.Now.AddDays(-20));
        h.AddNote("# 笔记\n文档在这里", updatedAt: h.Now.AddDays(-1));

        h.ViewModel.Query = "文档";
        h.ViewModel.SortByModifiedTime = true;
        h.ViewModel.SortByModifiedTime = false;

        // 回到相关度：标题命中的在前。
        Assert.Equal(oldTitleHit.Id, h.ViewModel.Items[0].Note.Id);
    }

    [Fact]
    public void 计数_没有搜索与筛选时只有总数()
    {
        using var h = new Harness();
        h.AddNote("# 一");
        h.AddNote("# 二");
        h.ViewModel.Refresh();

        Assert.Equal("2", h.ViewModel.CountText);
    }

    [Fact]
    public void 计数_搜索后是命中数比总数()
    {
        using var h = new Harness();
        h.AddNote("# 文档");
        h.AddNote("# 别的");

        h.ViewModel.Query = "文档";

        Assert.Equal("1/2", h.ViewModel.CountText);
    }

    [Fact]
    public void 计数_颜色筛选也算筛选态()
    {
        using var h = new Harness();
        Note blue = h.AddNote("# 蓝签");
        h.AddNote("# 黄签");
        blue.Color = NoteColor.Blue;

        h.ViewModel.ColorFilter = NoteColor.Blue;

        Assert.Equal("1/2", h.ViewModel.CountText);
    }

    [Fact]
    public void 标题命中_标题摘要带高亮段()
    {
        using var h = new Harness();
        h.AddNote("# 文档");
        h.AddNote("# 笔记\n无关内容");

        h.ViewModel.Query = "文档";

        NoteListItem item = Assert.Single(h.ViewModel.Items);
        Assert.Contains(item.TitleSnippet, static segment => segment.IsMatch);
    }

    [Fact]
    public async Task 改色_落盘并刷新列表()
    {
        using var h = new Harness();
        Note note = h.AddNote("# 文档");

        await h.ViewModel.ChangeColorAsync(note, NoteColor.Blue);

        Assert.Equal(NoteColor.Blue, Assert.Single(h.ViewModel.Items).Note.Color);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public async Task 复制_副本进入列表且内容原样()
    {
        using var h = new Harness();
        Note note = h.AddNote("# 文档");
        note.Color = NoteColor.Purple;

        Note copy = await h.ViewModel.DuplicateNoteAsync(note);

        Assert.NotEqual(note.Id, copy.Id);
        Assert.Equal("# 文档", copy.Content);
        Assert.Equal(NoteColor.Purple, copy.Color);
        Assert.Equal(2, h.ViewModel.Items.Count);
        Assert.Contains(copy.Id, h.ViewModel.Items.Select(static item => item.Note.Id));
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

            var toolbar = new ToolbarPreferences(
                new AppSettings(), new FakeSettingsStore(), NullLogger<ToolbarPreferences>.Instance);
            var factory = new NoteWindowFactory(
                Storage, Clock, Layouts, _autoSave, _titles, toolbar, NullLoggerFactory.Instance);
            Windows = new NoteWindowManager(Notes, Storage, Trash, Layouts, _titles, factory);

            ViewModel = new ManagerViewModel(
                new KeywordSearchProvider(), Layouts, Clock, Windows);
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
