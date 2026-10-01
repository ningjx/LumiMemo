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

    private sealed class FakeGenerator : ITitleGenerator
    {
        public Task<string> GenerateAsync(string content, LlmSettings settings,
            CancellationToken ct = default) => Task.FromResult("生成的标题");
    }
}
