using System.IO;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.Tests.TestDoubles;
using LumiMemo.WinUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.WinUI.Tests.ViewModels;

/// <summary>
/// <see cref="NoteViewModel"/> 的保存编排、状态文案与置顶镜像。
/// </summary>
public sealed class NoteViewModelTests
{
    [Fact]
    public void 编辑_更新模型并置正在保存()
    {
        using var h = new Harness();

        h.ViewModel.ApplyUserEdit("你好");

        Assert.Equal("你好", h.Note.Content);
        Assert.Equal("正在保存 · 2 字", h.ViewModel.StatusText);
        Assert.True(h.ViewModel.HasPendingSave);
        Assert.Single(h.Timers.Created);
    }

    [Fact]
    public async Task 落盘_状态回已保存且RTF先进模型()
    {
        using var h = new Harness();
        h.Document.Rtf = [1, 2, 3, 250, 255];
        h.ViewModel.AttachDocument(h.Document);
        h.ViewModel.ApplyUserEdit("结尾");

        Assert.True(await h.ViewModel.PersistAsync());

        Assert.Equal("已保存 · 2 字", h.ViewModel.StatusText);
        Assert.False(h.ViewModel.HasPendingSave);
        Assert.Single(h.Storage.Saved);
        Assert.Equal(new byte[] { 1, 2, 3, 250, 255 }, h.Note.RichTextContent);
    }

    [Fact]
    public async Task 保存失败_状态为失败且保留待存()
    {
        using var h = new Harness();
        h.Storage.SaveException = new IOException("磁盘满了");
        h.ViewModel.ApplyUserEdit("内容");

        Assert.False(await h.ViewModel.PersistAsync());

        Assert.Equal("保存失败 · 2 字", h.ViewModel.StatusText);
        Assert.True(h.ViewModel.HasPendingSave);
    }

    [Fact]
    public async Task 失败之后再编辑_重排一轮()
    {
        using var h = new Harness();
        h.Storage.SaveException = new IOException();
        h.ViewModel.ApplyUserEdit("甲");
        await h.ViewModel.PersistAsync();

        h.Storage.SaveException = null;
        h.ViewModel.ApplyUserEdit("乙");

        // 同一张便签复用同一个定时器：第一次编辑 + 失败后这次编辑各起一轮。
        ManualUiTimer timer = Assert.Single(h.Timers.Created);
        Assert.Equal(2, timer.StartCount);
        Assert.True(await h.ViewModel.PersistAsync());
        Assert.Equal("已保存 · 1 字", h.ViewModel.StatusText);
    }

    [Fact]
    public async Task 关窗保存_没改动直接放行()
    {
        using var h = new Harness();

        Assert.True(await h.ViewModel.TryPersistOnCloseAsync());
        Assert.Empty(h.Storage.Saved);
    }

    [Fact]
    public async Task 关窗保存_失败时返回放行否()
    {
        using var h = new Harness();
        h.Storage.SaveException = new IOException();
        h.ViewModel.ApplyUserEdit("内容");

        Assert.False(await h.ViewModel.TryPersistOnCloseAsync());
    }

    [Fact]
    public void 置顶_写入布局并标脏()
    {
        using var h = new Harness();

        h.ViewModel.IsTopMost = true;

        Assert.True(h.Layout.IsTopMost);
        Assert.Equal(1, h.Layouts.MarkDirtyCount);
    }

    [Fact]
    public void 编辑区事件_与直接编辑走同一条路()
    {
        using var h = new Harness();
        h.ViewModel.AttachDocument(h.Document);

        h.Document.Type("从编辑区来的");

        Assert.Equal("从编辑区来的", h.Note.Content);
        Assert.True(h.ViewModel.HasPendingSave);
    }

    [Fact]
    public async Task 标题生成失败_状态条给出提示()
    {
        using var h = new Harness(llmEnabled: true, generator: new ThrowingGenerator());
        h.ViewModel.ApplyUserEdit("今天开会讨论了下周的产品计划与任务安排");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await h.ViewModel.PersistAsync();

        // 协调器先等 900ms 再请求模型；模型抛错后文案落到状态条上。
        await WaitUntilAsync(
            () => h.ViewModel.StatusText.Contains("自动标题失败"),
            cancellation.Token);
    }

    [Fact]
    public async Task 保存中再编辑_完成后重排()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Storage.SaveGate = gate.Task;

        h.ViewModel.ApplyUserEdit("第一版");
        Task<bool> saving = h.ViewModel.PersistAsync();

        // 保存卡在网盘上时用户又敲了一个字：这一版要重新排一轮。
        h.ViewModel.ApplyUserEdit("第二版");
        gate.SetResult();
        Assert.True(await saving);

        Assert.True(h.ViewModel.HasPendingSave);
        Assert.True(await h.ViewModel.PersistAsync());
        Assert.Equal("第二版", h.Note.Content);
        Assert.Equal(2, h.Storage.Saved.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(25, ct);
        }
    }

    private sealed class ThrowingGenerator : ITitleGenerator
    {
        public Task<string> GenerateAsync(string content, LlmSettings settings, CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidDataException("模型没返回标题。"));
    }

    private sealed class NeverGenerator : ITitleGenerator
    {
        public Task<string> GenerateAsync(string content, LlmSettings settings, CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidOperationException("测试里 LLM 是关的，不该走到这里。"));
    }

    /// <summary>一张便签的整套替身与 ViewModel。</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(bool llmEnabled = false, ITitleGenerator? generator = null)
        {
            Note = new Note
            {
                Id = Guid.NewGuid(),
                FilePath = @"D:\notes\测试.lumi",
                Content = string.Empty,
            };
            Layout = new NoteLayout { NoteId = Note.Id };
            Storage = new FakeNoteStorage();
            Timers = new ManualUiTimerFactory();
            Clock = new FakeClock();
            Layouts = new InMemoryLayoutStore();
            Layouts.GetOrCreate(Note.Id);
            Document = new FakeRichDocument();

            AutoSave = new AutoSaveService(Timers, NullLogger<AutoSaveService>.Instance);

            var settings = new AppSettings
            {
                Llm = new LlmSettings
                {
                    Enabled = llmEnabled,
                    Model = llmEnabled ? "test" : string.Empty,
                },
            };

            Titles = new NoteTitleCoordinator(
                [Note], Storage, Clock, settings, generator ?? new NeverGenerator());

            ViewModel = new NoteViewModel(
                Note,
                Layout,
                Storage,
                Clock,
                Layouts,
                AutoSave,
                Titles,
                () => { },
                NullLogger<NoteViewModel>.Instance);
        }

        public Note Note { get; }

        public NoteLayout Layout { get; }

        public FakeNoteStorage Storage { get; }

        public ManualUiTimerFactory Timers { get; }

        public FakeClock Clock { get; }

        public InMemoryLayoutStore Layouts { get; }

        public FakeRichDocument Document { get; }

        public AutoSaveService AutoSave { get; }

        public NoteTitleCoordinator Titles { get; }

        public NoteViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            Titles.Dispose();
            AutoSave.Dispose();
        }
    }
}
