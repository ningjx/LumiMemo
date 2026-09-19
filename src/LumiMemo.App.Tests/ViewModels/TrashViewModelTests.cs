using System.IO;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using Xunit;

namespace LumiMemo.App.Tests.ViewModels;

/// <summary>
/// 回收站页签的行为（§7.3、§7.4）。
/// </summary>
/// <remarks>
/// <para>
/// 真实的搬运、索引对账与保留期由 <c>FileSystemTrashStoreTests</c> 与
/// <c>TrashServiceTests</c> 覆盖。这里只管<strong>界面这一层</strong>：
/// 什么时候问用户、问几句、用户答了之后转发什么出去。
/// </para>
/// <para>
/// 「列出的条目长什么样」不在这里验——那是 <c>TrashItem</c> 的事。
/// </para>
/// </remarks>
public sealed class TrashViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 刷新_把回收站里的条目灌进列表()
    {
        using Harness h = CreateHarness();
        h.Store.Add("归档/周报.md", size: 2048);
        h.Store.Add("随手记.md", size: 512);

        await h.Vm.RefreshAsync(Ct);

        Assert.Equal(2, h.Vm.Items.Count);
        Assert.Equal("周报", h.Vm.Items[0].Title);
        Assert.Equal(2560, h.Vm.TotalBytes);
        Assert.Equal("回收站里有 2 个项目（共 2.5 KB）", h.Vm.CountText);
    }

    [Fact]
    public async Task 刷新_回收站为空时计数说空而不是零()
    {
        using Harness h = CreateHarness();

        await h.Vm.RefreshAsync(Ct);

        // 「回收站里有 0 个项目」读起来像故障，不像「没东西」。
        Assert.Empty(h.Vm.Items);
        Assert.Equal("回收站是空的", h.Vm.CountText);
    }

    [Fact]
    public async Task 刷新_重新读一遍会把上一次的结果换掉而不是追加()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md");
        await h.Vm.RefreshAsync(Ct);

        h.Store.Entries.Clear();
        h.Store.Add("b.md");
        await h.Vm.RefreshAsync(Ct);

        TrashItem item = Assert.Single(h.Vm.Items);
        Assert.Equal("b", item.Title);
    }

    [Fact]
    public async Task 恢复_原位置空着时直接回原位且不问用户()
    {
        using Harness h = CreateHarness();
        TrashEntry entry = h.Store.Add("周报.md");
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.RestoreAsync(h.Vm.Items[0], Ct);

        Assert.Empty(h.Dialogs.ChooseRequests);

        // 目标传 null 才是「回原位」——存储层据此拿 OriginalRelativePath 当落点。
        (TrashEntry restored, string? target) = Assert.Single(h.Store.Restores);
        Assert.Same(entry, restored);
        Assert.Null(target);
    }

    [Fact]
    public async Task 恢复_成功后回收站列表里那一条就不见了()
    {
        using Harness h = CreateHarness();
        h.Store.Add("周报.md");
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.RestoreAsync(h.Vm.Items[0], Ct);

        // RestoreAsync 内部会自己再刷新一次，不需要用例手动来一遍。
        Assert.Empty(h.Vm.Items);
    }

    [Fact]
    public async Task 恢复_原位置被占用时给三档选项_选重命名就回原位()
    {
        using Harness h = CreateHarness();
        TrashEntry entry = h.Store.Add("归档/周报.md");
        h.Store.Occupied.Add(entry.TrashName);
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.RestoreAsync(h.Vm.Items[0], Ct);

        string request = Assert.Single(h.Dialogs.ChooseRequests);
        Assert.Contains("恢复到原位置并重命名", request, StringComparison.Ordinal);
        Assert.Contains("恢复到笔记目录根", request, StringComparison.Ordinal);
        Assert.Contains("取消", request, StringComparison.Ordinal);

        (_, string? target) = Assert.Single(h.Store.Restores);
        Assert.Null(target);
    }

    [Fact]
    public async Task 恢复_选恢复到笔记目录根时目标只取文件名()
    {
        using Harness h = CreateHarness();
        TrashEntry entry = h.Store.Add("归档/2026/周报.md");
        h.Store.Occupied.Add(entry.TrashName);
        h.Dialogs.ChooseResult = 1;
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.RestoreAsync(h.Vm.Items[0], Ct);

        (_, string? target) = Assert.Single(h.Store.Restores);

        // 只取最后一段：带着目录走的话，「恢复到根」就跟「回原位」没区别了。
        Assert.Equal("周报.md", target);
    }

    [Fact]
    public async Task 恢复_选取消或直接关掉对话框都什么都不做()
    {
        // -1 是「关掉对话框没有作答」，它与「取消」对调用方是同一件事（见 IDialogService.ChooseAsync）。
        foreach (int choice in new[] { 2, -1 })
        {
            using Harness h = CreateHarness();
            TrashEntry entry = h.Store.Add("周报.md");
            h.Store.Occupied.Add(entry.TrashName);
            h.Dialogs.ChooseResult = choice;
            await h.Vm.RefreshAsync(Ct);

            await h.Vm.RestoreAsync(h.Vm.Items[0], Ct);

            Assert.Empty(h.Store.Restores);
            Assert.Single(h.Vm.Items);
        }
    }

    [Fact]
    public async Task 恢复_没选中任何一行时什么都不做也不问()
    {
        using Harness h = CreateHarness();
        h.Store.Add("周报.md");
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.RestoreAsync(null, Ct);

        Assert.Empty(h.Dialogs.ChooseRequests);
        Assert.Empty(h.Store.Restores);
    }

    [Fact]
    public async Task 清空_两次都确认了才真的清()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md", size: 1024);
        h.Store.Add("b.md", size: 1024);
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.EmptyAsync(Ct);

        Assert.Equal(2, h.Dialogs.ConfirmRequests.Count);
        Assert.Equal(1, h.Store.EmptyCallCount);
        Assert.Empty(h.Vm.Items);
    }

    [Fact]
    public async Task 清空_第一次确认里要摆出数量与体积()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md", size: 1024 * 1024);
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.EmptyAsync(Ct);

        string first = h.Dialogs.ConfirmRequests[0];
        Assert.Contains("1 个项目", first, StringComparison.Ordinal);
        Assert.Contains("1 MB", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 清空_第二次的按钮文字是永久删除而不是确定()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md");
        await h.Vm.RefreshAsync(Ct);

        await h.Vm.EmptyAsync(Ct);

        // 这是全程序唯一不可逆的一步，用户点错时不该有看错的余地（§7.4）。
        Assert.Equal(2, h.Dialogs.ConfirmTexts.Count);

        (string confirmText, string cancelText, string message) = h.Dialogs.ConfirmTexts[1];
        Assert.Equal("永久删除", confirmText);
        Assert.Equal("取消", cancelText);
        Assert.Equal("此操作不可撤销，确定要永久删除吗？", message);
    }

    [Fact]
    public async Task 清空_第一步就拒绝时一个都不删()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md");
        await h.Vm.RefreshAsync(Ct);

        h.Dialogs.ConfirmResult = false;
        await h.Vm.EmptyAsync(Ct);

        // 只问了第一次，第二次根本没到。
        Assert.Single(h.Dialogs.ConfirmRequests);
        Assert.Equal(0, h.Store.EmptyCallCount);
        Assert.Single(h.Vm.Items);
    }

    [Fact]
    public async Task 清空_第二步拒绝时也一个都不删()
    {
        using Harness h = CreateHarness();
        h.Store.Add("a.md");
        await h.Vm.RefreshAsync(Ct);

        // 第一次答「是」、第二次答「否」——这正是「看清了体积又改了主意」的那条路。
        int asked = 0;
        h.Dialogs.ConfirmHandler = (_, _) => ++asked == 1;
        await h.Vm.EmptyAsync(Ct);

        Assert.Equal(2, h.Dialogs.ConfirmRequests.Count);
        Assert.Equal(0, h.Store.EmptyCallCount);
        Assert.Single(h.Vm.Items);
    }

    [Fact]
    public async Task 清空_回收站本来就空时一句都不问()
    {
        using Harness h = CreateHarness();

        await h.Vm.EmptyAsync(Ct);

        // 空回收站上弹两次「确定要永久删除吗」纯粹是骚扰。
        Assert.Empty(h.Dialogs.ConfirmRequests);
        Assert.Equal(0, h.Store.EmptyCallCount);
    }

    [Fact]
    public async Task 打开回收站目录_把路径交给外壳()
    {
        using Harness h = CreateHarness();

        await h.Vm.OpenTrashFolderAsync(Ct);

        // 目录不存在也要先建出来，否则「点了一下什么都没发生」会被读成按钮坏了。
        string opened = Assert.Single(h.Shell.OpenedFolders);
        Assert.Equal(Path.Combine(h.NotesFolder!, ".lumimemo", "trash"), opened);
        Assert.True(Directory.Exists(opened), "点「打开回收站目录」应当把目录建出来。");
    }

    [Fact]
    public async Task 打开回收站目录_外壳拒绝时提示用户()
    {
        using Harness h = CreateHarness();
        h.Shell.Result = false;

        await h.Vm.OpenTrashFolderAsync(Ct);

        Assert.Single(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public async Task 打开回收站目录_没选笔记目录时只给一句提示不碰外壳()
    {
        using Harness h = CreateHarness(withNotesFolder: false);

        await h.Vm.OpenTrashFolderAsync(Ct);

        Assert.Single(h.Dialogs.InfoRequests);
        Assert.Empty(h.Shell.OpenedFolders);
    }

    /// <param name="withNotesFolder">
    /// <see langword="false"/> 时模拟「用户尚未选定笔记目录」。
    /// </param>
    /// <remarks>
    /// 笔记目录指向一个<strong>真的临时目录</strong>，而不是 <c>D:\notes</c> 这种写死的路径：
    /// 「打开回收站目录」那条路会真的调 <c>Directory.CreateDirectory</c>，
    /// 拿一个假路径去跑就会在跑测试的这台机器上建出目录来——盘符不存在时还会直接失败。
    /// </remarks>
    private static Harness CreateHarness(bool withNotesFolder = true) =>
        new(withNotesFolder ? NewTempFolder() : null);

    private static string NewTempFolder()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "LumiMemo.App.Tests", Guid.NewGuid().ToString("N"));

        return Directory.CreateDirectory(path).FullName;
    }

    /// <summary>一套装好的回收站页签与它的全部替身。</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(string? notesFolder)
        {
            NotesFolder = notesFolder;

            var paths = new FakeAppPaths(notesFolder);

            var service = new TrashService(
                Store, paths, new NoteStore(), new SearchIndex(), new FakeNoteRepository());

            Vm = new TrashViewModel(service, paths, Dialogs, Shell, new ImmediateDispatcher());
        }

        /// <summary>本用例的笔记目录；<see langword="null"/> 表示尚未选定。</summary>
        public string? NotesFolder { get; }

        public FakeTrashStore Store { get; } = new();

        public RecordingDialogService Dialogs { get; } = new();

        public RecordingShellLauncher Shell { get; } = new();

        public TrashViewModel Vm { get; }

        /// <summary>
        /// 删掉临时目录。
        /// </summary>
        /// <remarks>
        /// 失败被吞掉是刻意的：Windows 上索引器或杀毒软件都可能让 <c>Directory.Delete</c> 抛异常，
        /// 为此把一个已经通过断言的用例变红只会掩盖真正的失败。残留目录交给系统清理。
        /// </remarks>
        public void Dispose()
        {
            if (NotesFolder is null)
            {
                return;
            }

            try
            {
                Directory.Delete(NotesFolder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
