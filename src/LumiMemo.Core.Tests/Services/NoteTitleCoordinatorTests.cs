using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

public sealed class NoteTitleCoordinatorTests
{
    [Fact]
    public async Task SavedNoteReceivesTitleAfterEditorIsGone()
    {
        var note = new Note { Id = Guid.NewGuid(), FilePath = "note.lumi", Content = "" };
        var storage = new FakeNoteStorage();
        var settings = new AppSettings { Llm = new LlmSettings { Enabled = true, Model = "test" } };
        using var coordinator = new NoteTitleCoordinator(
            [note], storage, new FakeClock(), settings, new FakeGenerator());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TitleUpdated += _ => completed.TrySetResult();

        note.Content = "今天开会讨论了下周的产品计划和任务";
        coordinator.ContentChanged(note);
        coordinator.ContentSaved(note);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal("生成的标题", note.AutoTitle);
        Assert.Single(storage.Saved);
        Assert.False(coordinator.IsPending(note.Id));
    }

    [Fact]
    public async Task 手动重生成_已有标题且内容很短也强制生成()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            FilePath = "note.lumi",
            Content = "太短",
            AutoTitle = "旧标题",
        };
        var storage = new FakeNoteStorage();
        var settings = new AppSettings { Llm = new LlmSettings { Enabled = true, Model = "test" } };
        using var coordinator = new NoteTitleCoordinator(
            [note], storage, new FakeClock(), settings, new FakeGenerator());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TitleUpdated += _ => completed.TrySetResult();

        // 常规策略会拒绝（内容不足 12 字 + 已有标题），手动刷新要绕过它。
        Assert.True(coordinator.Regenerate(note));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal("生成的标题", note.AutoTitle);
        Assert.Single(storage.Saved);
        Assert.False(coordinator.IsPending(note.Id));
    }

    [Fact]
    public void 手动重生成_未启用自动标题或没配模型时拒绝()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            FilePath = "note.lumi",
            Content = "内容够长的一行文字",
        };
        var settings = new AppSettings { Llm = new LlmSettings { Enabled = false, Model = "test" } };
        using var coordinator = new NoteTitleCoordinator(
            [note], new FakeNoteStorage(), new FakeClock(), settings, new FakeGenerator());

        Assert.False(coordinator.Regenerate(note));
        Assert.False(coordinator.IsPending(note.Id));

        settings.Llm = new LlmSettings { Enabled = true, Model = string.Empty };
        Assert.False(coordinator.Regenerate(note));
    }

    [Fact]
    public async Task 手动重生成_失败时走失败事件并复位()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            FilePath = "note.lumi",
            Content = "内容够长的一行文字",
        };
        var settings = new AppSettings { Llm = new LlmSettings { Enabled = true, Model = "test" } };
        using var coordinator = new NoteTitleCoordinator(
            [note],
            new FakeNoteStorage(),
            new FakeClock(),
            settings,
            new FakeGenerator { Exception = new HttpRequestException("模型不可达") });
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.GenerationFailed += _ => failed.TrySetResult();

        Assert.True(coordinator.Regenerate(note));

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Null(note.AutoTitle);
        Assert.False(coordinator.IsPending(note.Id));
    }

    [Fact]
    public async Task 手动重生成_进行中再次触发时旧请求作废()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            FilePath = "note.lumi",
            Content = "内容够长的一行文字",
        };
        var settings = new AppSettings { Llm = new LlmSettings { Enabled = true, Model = "test" } };
        var generator = new GatedGenerator();
        using var coordinator = new NoteTitleCoordinator(
            [note], new FakeNoteStorage(), new FakeClock(), settings, generator);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TitleUpdated += _ => completed.TrySetResult();

        Assert.True(coordinator.Regenerate(note));   // 第一次：卡在门后
        Assert.True(coordinator.Regenerate(note));   // 第二次：作废第一次

        generator.Gate.SetResult();                  // 放行：只有第二次的结果能落地

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal("标题2", note.AutoTitle);
    }

    private sealed class FakeGenerator : ITitleGenerator
    {
        public Exception? Exception { get; set; }

        public Task<string> GenerateAsync(string content, LlmSettings settings,
            CancellationToken ct = default) => Exception is { } exception
                ? Task.FromException<string>(exception)
                : Task.FromResult("生成的标题");
    }

    /// <summary>可以卡在门后等待的生成器：用来编排"生成进行中"的时序。</summary>
    private sealed class GatedGenerator : ITitleGenerator
    {
        private int _calls;

        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> GenerateAsync(string content, LlmSettings settings,
            CancellationToken ct = default)
        {
            int call = Interlocked.Increment(ref _calls);

            await Gate.Task.WaitAsync(ct);

            return $"标题{call}";
        }
    }
}
