using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

public sealed class TitleChangePolicyTests
{
    [Fact]
    public void NewNoteStartsGenerationAfterEnoughContent() =>
        Assert.True(TitleChangePolicy.ShouldGenerate("", "今天开会讨论了下周的产品计划和任务", false));

    [Fact]
    public void OrdinaryAppendDoesNotRegenerate() =>
        Assert.False(TitleChangePolicy.ShouldGenerate(
            "今天开会讨论了下周的产品计划和任务",
            "今天开会讨论了下周的产品计划和任务，还确认了时间", true));

    [Fact]
    public void ReplacingBodyRegenerates() =>
        Assert.True(TitleChangePolicy.ShouldGenerate(
            "今天开会讨论了下周的产品计划和任务",
            "采购咖啡豆纸巾牛奶面包鸡蛋和水果", true));

    [Fact]
    public void EmptyBodyDoesNotRegenerate() =>
        Assert.False(TitleChangePolicy.ShouldGenerate("原本的正文有很多内容需要处理", "", true));

    [Fact]
    public void AutoTitleOverridesDerivedTitleWithoutChangingBody()
    {
        var note = new Note { Id = Guid.NewGuid(), FilePath = "test.lumi", Content = "原正文" };
        note.AutoTitle = "模型标题";
        Assert.Equal("模型标题", note.Title);
        Assert.Equal("原正文", note.Content);
        note.AutoTitle = null;
        Assert.Equal("原正文", note.Title);
    }
}
