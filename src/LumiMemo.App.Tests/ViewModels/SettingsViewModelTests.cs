using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using Xunit;

namespace LumiMemo.App.Tests.ViewModels;

/// <summary>
/// 设置窗口的读、改、存（§15.9）。
/// </summary>
/// <remarks>
/// <para>
/// 保存之后的钳制边界<strong>本身</strong>（读入时兜底钳一次、<c>9999</c> 会变成 <c>800</c>）
/// 由 <c>JsonSettingsStoreTests</c> 用真实文件覆盖。这里验的是设置窗口的策略：
/// <strong>保存前自己先钳一遍</strong>，让用户当场看到被改成了多少，
/// 而不是下次启动时被悄悄改掉。
/// </para>
/// <para>
/// 「保存之后有没有灌进对象图」靠 <see cref="RecordingSettingsApplier"/> 验。
/// 生产的应用器是 <c>StartupSequence</c>，它的十二个依赖里包括窗口，在无头测试里建不起来。
/// </para>
/// </remarks>
public sealed class SettingsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 载入_把磁盘上的值灌进各字段()
    {
        Harness h = CreateHarness();
        h.Store.Current = new AppSettings
        {
            DefaultWidth = 500,
            DefaultHeight = 600,
            DefaultContentScale = 1.25,
            ShowStatusBar = false,
            RestoreAfterShowDesktop = false,
            AutoSaveDelayMs = 700,
            SearchDebounceMs = 300,
            TrashRetentionDays = 7,
        };

        await h.Vm.LoadAsync(Ct);

        Assert.Equal(500d, h.Vm.DefaultWidth);
        Assert.Equal(600d, h.Vm.DefaultHeight);
        Assert.Equal(1.25, h.Vm.ContentScale);
        Assert.False(h.Vm.ShowStatusBar);
        Assert.False(h.Vm.RestoreAfterShowDesktop);
        Assert.Equal(700, h.Vm.AutoSaveDelayMs);
        Assert.Equal(300, h.Vm.SearchDebounceMs);
        Assert.Equal(7, h.Vm.SelectedRetention.Days);
    }

    [Fact]
    public async Task 载入_保留期不在可选档位里时退回三十天()
    {
        Harness h = CreateHarness();

        // 用户手改过 settings.json，或者那份配置来自一个档位不同的旧版本。
        h.Store.Current = new AppSettings { TrashRetentionDays = 14 };

        await h.Vm.LoadAsync(Ct);

        // 不能留一个「什么都没选中」的下拉框，也不能凭空造出一个「14 天」的选项。
        Assert.Equal(30, h.Vm.SelectedRetention.Days);
    }

    [Fact]
    public async Task 载入_把回收站页签也刷一遍()
    {
        Harness h = CreateHarness();
        h.Trash.Add("周报.md");

        await h.Vm.LoadAsync(Ct);

        TrashItem item = Assert.Single(h.Vm.Trash.Items);
        Assert.Equal("周报", item.Title);
    }

    [Fact]
    public async Task 载入_清掉上一次遗留的状态文字()
    {
        Harness h = CreateHarness();
        await h.Vm.SaveAsync(Ct);
        Assert.Equal("已保存", h.Vm.StatusText);

        await h.Vm.LoadAsync(Ct);

        // 重新打开窗口时底下还挂着「已保存」会让人以为这次也已经存过了。
        Assert.Equal(string.Empty, h.Vm.StatusText);
    }

    [Fact]
    public async Task 保存_越界的值被钳回范围内()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        h.Vm.DefaultWidth = 5;
        h.Vm.DefaultHeight = 99999;
        h.Vm.ContentScale = 12;
        h.Vm.AutoSaveDelayMs = 5000;
        h.Vm.SearchDebounceMs = 1;

        await h.Vm.SaveAsync(Ct);

        Assert.Equal(SettingsViewModel.MinDefaultSize, h.Vm.DefaultWidth);
        Assert.Equal(SettingsViewModel.MaxDefaultSize, h.Vm.DefaultHeight);
        Assert.Equal(SettingsViewModel.MaxContentScale, h.Vm.ContentScale);
        Assert.Equal(JsonSettingsStore.MaxAutoSaveDelayMs, h.Vm.AutoSaveDelayMs);
        Assert.Equal(JsonSettingsStore.MinSearchDebounceMs, h.Vm.SearchDebounceMs);
    }

    [Fact]
    public async Task 保存_先把钳制结果写回界面而不是等下次启动()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        h.Vm.AutoSaveDelayMs = 1000;
        await h.Vm.SaveAsync(Ct);

        // 用户填了 1000，看到的必须是 800——直接落盘的话他会以为「我明明填了 1000」。
        Assert.Equal(800, h.Vm.AutoSaveDelayMs);
        Assert.Equal(800, h.Store.Saved[0].AutoSaveDelayMs);
    }

    [Fact]
    public async Task 保存_页面上没显示的字段原样带着走()
    {
        Harness h = CreateHarness();
        h.Store.Current = new AppSettings
        {
            Theme = "dark",
            GlobalQuickCaptureHotkey = "Ctrl+Alt+Q",
            LogLevel = "Debug",
            StartWithWindows = true,
            NotesFolder = @"C:\用户改过的\笔记",
        };

        await h.Vm.LoadAsync(Ct);
        h.Vm.DefaultWidth = 400;
        await h.Vm.SaveAsync(Ct);

        AppSettings saved = h.Store.Saved[0];

        // 这些字段本轮没有画出来。若保存时重新 new 一份设置，用户改一次宽度
        // 就会把主题、热键、日志级别一起重置——而且是静默的。
        Assert.Equal("dark", saved.Theme);
        Assert.Equal("Ctrl+Alt+Q", saved.GlobalQuickCaptureHotkey);
        Assert.Equal("Debug", saved.LogLevel);
        Assert.True(saved.StartWithWindows);
        Assert.Equal(@"C:\用户改过的\笔记", saved.NotesFolder);
    }

    [Fact]
    public async Task 保存_把新的那份推给应用器让它立刻生效()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        h.Vm.ShowStatusBar = false;
        await h.Vm.SaveAsync(Ct);

        AppSettings applied = Assert.Single(h.Applier.Applied);
        Assert.False(applied.ShowStatusBar);
        Assert.Same(h.Store.Saved[0], applied);
    }

    [Fact]
    public async Task 保存_成功之后状态文字变成已保存()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        await h.Vm.SaveAsync(Ct);

        Assert.Equal("已保存", h.Vm.StatusText);
    }

    [Fact]
    public async Task 保存_没载入过就直接点保存时先补读一次磁盘()
    {
        Harness h = CreateHarness();
        h.Store.Current = new AppSettings { Theme = "dark", LogLevel = "Warning" };

        // 构造完窗口就点保存，载入命令还没跑完。_settings 还是 null。
        await h.Vm.SaveAsync(Ct);

        Assert.Equal(1, h.Store.LoadCallCount);

        // 补读的意义就在这里：不补的话这两项会被一份空设置覆盖成默认值。
        Assert.Equal("dark", h.Store.Saved[0].Theme);
        Assert.Equal("Warning", h.Store.Saved[0].LogLevel);
    }

    [Fact]
    public async Task 载入之后再保存不会重复读磁盘()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        await h.Vm.SaveAsync(Ct);
        await h.Vm.SaveAsync(Ct);

        // 已经拿到那一份了就一直在手里改，不必每次保存都去读一遍。
        Assert.Equal(1, h.Store.LoadCallCount);
    }

    [Fact]
    public async Task 保存_保留期按选中的那一档写下去()
    {
        Harness h = CreateHarness();
        await h.Vm.LoadAsync(Ct);

        h.Vm.SelectedRetention = h.Vm.RetentionOptions.Single(option => option.Days == 0);
        await h.Vm.SaveAsync(Ct);

        // 0 是「永不清理」，不是「立刻清空」（§7.4）。写下去的必须是 0 本身。
        Assert.Equal(0, h.Store.Saved[0].TrashRetentionDays);
    }

    [Fact]
    public void 保留期档位_永远清理这一档不写成零天()
    {
        Harness h = CreateHarness();

        RetentionOption never = h.Vm.RetentionOptions.Single(option => option.Days == 0);

        // 界面上写「0 天」会被读成「立刻清空」，与它「永不清理」的实际语义正好相反。
        Assert.Equal("永不清理", never.Name);
    }

    [Fact]
    public async Task 打开笔记文件夹_把路径交给外壳()
    {
        Harness h = CreateHarness();

        await h.Vm.OpenNotesFolderAsync(Ct);

        Assert.Equal(@"D:\notes", Assert.Single(h.Shell.OpenedFolders));
        Assert.Empty(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public async Task 打开笔记文件夹_没选目录时只给一句提示不碰外壳()
    {
        Harness h = CreateHarness(withNotesFolder: false);

        await h.Vm.OpenNotesFolderAsync(Ct);

        Assert.Single(h.Dialogs.InfoRequests);
        Assert.Empty(h.Shell.OpenedFolders);
    }

    [Fact]
    public async Task 打开笔记文件夹_外壳拒绝时提示用户()
    {
        Harness h = CreateHarness();
        h.Shell.Result = false;

        await h.Vm.OpenNotesFolderAsync(Ct);

        Assert.Single(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public void 关闭_发出一个关闭请求()
    {
        Harness h = CreateHarness();
        int raised = 0;
        h.Vm.CloseRequested += (_, _) => raised++;

        h.Vm.CloseCommand.Execute(null);

        // ViewModel 不认识窗口（§18.3），它只发请求；窗口收到了自己去 Close。
        Assert.Equal(1, raised);
    }

    [Fact]
    public void 笔记文件夹文字_没选目录时也给一句人话()
    {
        Harness h = CreateHarness(withNotesFolder: false);

        Assert.Equal("（尚未选定）", h.Vm.NotesFolderText);
    }

    private static Harness CreateHarness(bool withNotesFolder = true)
    {
        var paths = new AppPaths(@"C:\fake-local");

        if (withNotesFolder)
        {
            paths.SetNotesFolder(@"D:\notes");
        }

        return new Harness(paths);
    }

    /// <summary>一套装好的设置窗口 ViewModel 与它的全部替身。</summary>
    private sealed class Harness
    {
        public Harness(AppPaths paths)
        {
            var trash = new TrashService(
                Trash, paths, new NoteStore(), new SearchIndex(), new FakeNoteRepository());

            Vm = new SettingsViewModel(
                Store, paths, trash, Applier, Dialogs, Shell, new ImmediateDispatcher());
        }

        public FakeSettingsStore Store { get; } = new();

        public FakeTrashStore Trash { get; } = new();

        public RecordingSettingsApplier Applier { get; } = new();

        public RecordingDialogService Dialogs { get; } = new();

        public RecordingShellLauncher Shell { get; } = new();

        public SettingsViewModel Vm { get; }
    }
}
