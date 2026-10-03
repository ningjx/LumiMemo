using System.IO;
using System.Text.Json;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="LumiNoteStorage"/> 的集成测试（私有 .lumi 格式，2026-10 决策）。
/// </summary>
/// <remarks>
/// 打真实文件系统，钉住两件事：保存-加载的字节保真，以及容错矩阵的每一行——
/// 一份坏文件不能拖垮整个启动。
/// </remarks>
public sealed class LumiNoteStorageTests
{
    private static readonly DateTimeOffset When =
        new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LumiNoteStorage Create(string folder) =>
        new(folder, new FakeClock(), new AtomicFileWriter(new FakeClock()),
            NoteColor.Yellow, NullLogger<LumiNoteStorage>.Instance);

    private static Note NewNote(string folder)
    {
        Guid id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = Path.Combine(folder, $"{id:N}.lumi"),
            Content = string.Empty,
            CreatedAt = When,
            UpdatedAt = When,
        };
    }

    /// <summary>
    /// 手工构造文件形态的 JSON。
    /// </summary>
    /// <remarks>
    /// 刻意不引用实现的私有 <c>StoredNote</c>：属性名（PascalCase）与色值（数字）
    /// 是<strong>格式契约</strong>——沿用自第一版 .lumi 文件的真实形态（现有用户文件
    /// 就是 <c>"Version"</c>、<c>"Color":0</c>）。这份测试手写契约，实现改坏契约时它就红。
    /// <para>
    /// <c>Version</c> 由调用方给：v2（当前）之外的版本用来验「跳过」路径——加载端自
    /// Phase 2 M7 起明确 <b>v1（RTF 权威）不迁移、直接跳过</b>，故凡期望「正常加载」的用例
    /// 都必须写当前版本，否则验的就成了版本跳过而不是用例本意。
    /// </para>
    /// </remarks>
    private static string BuildNoteJson(int version, Guid id, string text = "") =>
        JsonSerializer.Serialize(new
        {
            Version = version,
            Id = id,
            Text = text,
            Document = (byte[]?)null,
            Color = NoteColor.Yellow,
            Tags = Array.Empty<string>(),
            CreatedAt = When,
            UpdatedAt = When,
        });

    [Fact]
    public async Task 保存再加载_内容与权威内容逐字节保真()
    {
        using var temp = new TempDirectory();
        Note note = NewNote(temp.Path);
        note.Content = "会议记录：下周三上线。";
        note.RichTextContent = [1, 2, 3, 250, 255, 0];
        note.Color = NoteColor.Blue;
        note.AutoTitle = "上线安排";
        note.Tags.Add("工作");

        await Create(temp.Path).SaveAsync(note, Ct);
        IReadOnlyList<Note> loaded = await Create(temp.Path).LoadAllAsync(Ct);

        Note round = Assert.Single(loaded);
        Assert.Equal(note.Id, round.Id);
        Assert.Equal(note.FilePath, round.FilePath);
        Assert.Equal(note.Content, round.Content);
        Assert.Equal(note.RichTextContent, round.RichTextContent);
        Assert.Equal(NoteColor.Blue, round.Color);
        Assert.Equal(new[] { "工作" }, round.Tags);
        Assert.Equal("上线安排", round.AutoTitle);
        Assert.Equal(note.CreatedAt, round.CreatedAt);
        Assert.Equal(note.UpdatedAt, round.UpdatedAt);
    }

    [Fact]
    public async Task 创建_空便笺立刻落盘且用默认色()
    {
        using var temp = new TempDirectory();

        Note note = await Create(temp.Path).CreateAsync(ct: Ct);

        Assert.True(File.Exists(note.FilePath));
        Assert.Equal(temp.Path, Path.GetDirectoryName(note.FilePath));

        Note loaded = Assert.Single(await Create(temp.Path).LoadAllAsync(Ct));
        Assert.Equal(note.Id, loaded.Id);
        Assert.Equal(NoteColor.Yellow, loaded.Color);
        Assert.Equal(string.Empty, loaded.Content);
    }

    [Fact]
    public async Task 创建时目录不存在_抛异常且不凭空建目录()
    {
        using var temp = new TempDirectory();
        string missing = temp.Combine("被拔掉的移动盘");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(missing).CreateAsync(ct: Ct));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task 复制_内容原样换新id与新时间戳()
    {
        using var temp = new TempDirectory();
        Note source = NewNote(temp.Path);
        source.Content = "原文";
        source.RichTextContent = [9, 8, 7];
        source.Color = NoteColor.Purple;
        source.Tags.Add("工作");
        source.AutoTitle = "原题";

        await Create(temp.Path).SaveAsync(source, Ct);
        Note copy = await Create(temp.Path).DuplicateAsync(source, Ct);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.True(File.Exists(copy.FilePath));
        Assert.Equal(source.Content, copy.Content);
        Assert.Equal(new byte[] { 9, 8, 7 }, copy.RichTextContent);
        Assert.Equal(NoteColor.Purple, copy.Color);
        Assert.Equal(new[] { "工作" }, copy.Tags);
        Assert.Equal("原题", copy.AutoTitle);
        Assert.Equal(copy.CreatedAt, copy.UpdatedAt);

        // 源文件还在，复制件也落了盘：加载得到两份。
        IReadOnlyList<Note> loaded = await Create(temp.Path).LoadAllAsync(Ct);
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task 复制时目录不存在_抛异常()
    {
        using var temp = new TempDirectory();
        string missing = temp.Combine("还不存在");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(missing).DuplicateAsync(NewNote(missing), Ct));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task 坏文件被跳过_其余便笺照常加载()
    {
        using var temp = new TempDirectory();
        Note good = NewNote(temp.Path);
        good.Content = "正常的便笺";
        await Create(temp.Path).SaveAsync(good, Ct);

        File.WriteAllText(temp.Combine("坏掉的.lumi"), "{ 这不是 JSON");

        Note only = Assert.Single(await Create(temp.Path).LoadAllAsync(Ct));
        Assert.Equal(good.Id, only.Id);
    }

    [Fact]
    public async Task 版本高于当前_跳过()
    {
        using var temp = new TempDirectory();
        Guid id = Guid.NewGuid();
        File.WriteAllText(
            temp.Combine($"{id:N}.lumi"),
            BuildNoteJson(LumiNoteStorage.FormatVersion + 1, id));

        Assert.Empty(await Create(temp.Path).LoadAllAsync(Ct));
    }

    [Fact]
    public async Task id为空_跳过()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("空id.lumi"),
            BuildNoteJson(LumiNoteStorage.FormatVersion, Guid.Empty));

        Assert.Empty(await Create(temp.Path).LoadAllAsync(Ct));
    }

    [Fact]
    public async Task 文件名与id不符_以id为准且不改名()
    {
        using var temp = new TempDirectory();
        Guid id = Guid.NewGuid();
        string path = temp.Combine("名字对不上.lumi");
        File.WriteAllText(path, BuildNoteJson(LumiNoteStorage.FormatVersion, id, "名字对不上但内容有效"));

        Note loaded = Assert.Single(await Create(temp.Path).LoadAllAsync(Ct));

        Assert.Equal(id, loaded.Id);
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(temp.Path, "*.lumi"));
    }

    [Fact]
    public async Task 同id重复_取路径序第一个()
    {
        using var temp = new TempDirectory();
        Guid id = Guid.NewGuid();
        File.WriteAllText(temp.Combine("a-第一份.lumi"),
            BuildNoteJson(LumiNoteStorage.FormatVersion, id, "甲"));
        File.WriteAllText(temp.Combine("b-第二份.lumi"),
            BuildNoteJson(LumiNoteStorage.FormatVersion, id, "乙"));

        Note loaded = Assert.Single(await Create(temp.Path).LoadAllAsync(Ct));

        Assert.Equal("甲", loaded.Content);
    }

    [Fact]
    public async Task 加载时清理残留的临时文件()
    {
        using var temp = new TempDirectory();
        string leftover = temp.Combine("残骸.lumi.20261001-abcdef12" + AtomicFileWriter.TempSuffix);
        File.WriteAllText(leftover, "上次被强杀留下的半截文件");

        await Create(temp.Path).LoadAllAsync(Ct);

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task 目录不存在_返回空列表()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await Create(temp.Combine("还不存在")).LoadAllAsync(Ct));
    }

    [Fact]
    public async Task 返回按创建时间排序()
    {
        using var temp = new TempDirectory();
        Note later = NewNote(temp.Path);
        later.CreatedAt = When.AddHours(1);
        Note earlier = NewNote(temp.Path);
        earlier.CreatedAt = When;

        await Create(temp.Path).SaveAsync(later, Ct);
        await Create(temp.Path).SaveAsync(earlier, Ct);

        IReadOnlyList<Note> loaded = await Create(temp.Path).LoadAllAsync(Ct);

        Assert.Equal(new[] { earlier.Id, later.Id }, loaded.Select(note => note.Id));
    }
}
