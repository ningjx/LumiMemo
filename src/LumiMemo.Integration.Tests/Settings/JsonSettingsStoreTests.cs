using System.IO;
using System.Text.Json;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Settings;

/// <summary>
/// <see cref="JsonSettingsStore"/> 的集成测试（§8.2、§8.4、§9.3）。
/// </summary>
/// <remarks>
/// 这里打真实文件系统。要点是「文件以什么形态躺在磁盘上」——键名、缩进、中文是否被转义、
/// 坏文件被挪去了哪里。这些用内存流测不出来，而它们正是用户能看见的部分。
/// </remarks>
public sealed class JsonSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 首次运行 ----

    [Fact]
    public async Task 文件不存在时用默认值且不创建文件()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(1, settings.Version);
        Assert.Equal("", settings.NotesFolder);
        Assert.Equal(500, settings.AutoSaveDelayMs);
        Assert.Equal(150, settings.SearchDebounceMs);
        Assert.Equal(NoteColor.Yellow, settings.DefaultColor);

        // 「显示桌面后把便签拉回来」默认开（§13.6）：用户按 Win+D 想要的是桌面，
        // 不是「便签消失了」，所以新装默认就是开着的。
        Assert.True(settings.RestoreAfterShowDesktop);

        // 「首次运行」不是需要惊动用户的事，也不是「读不出来」。
        Assert.Null(store.LastProblem);

        // 读了但没改，就不该凭空造出一个文件——那会让「配置文件是什么时候出现的」
        // 这条排查线索失真。
        Assert.False(File.Exists(paths.SettingsFile));
    }

    // ---- 往返 ----

    [Fact]
    public async Task 全部字段保存后能原样读回()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);

        var saved = new AppSettings
        {
            NotesFolder = @"D:\我的笔记",
            AttachmentsFolderName = "files",
            Theme = "dark",
            DefaultColor = NoteColor.Purple,
            DefaultWidth = 400.5,
            DefaultHeight = 500.25,
            ShowStatusBar = false,
            RestoreAfterShowDesktop = false,
            StartWithWindows = true,
            MinimizeToTrayOnClose = false,
            ShowTrayIcon = false,
            SingleClickTrayAction = "newNote",
            GlobalQuickCaptureHotkey = "Ctrl+Alt+Q",
            GlobalShowAllHotkey = "Ctrl+Alt+A",
            AutoSaveDelayMs = 700,
            SearchDebounceMs = 400,
            TrashRetentionDays = 60,
            EnableAnimations = false,
            MaxOpenWindows = 20,
            LogLevel = "Debug",
        };

        await store.SaveAsync(saved, Ct);
        AppSettings loaded = await CreateStore(local).Store.LoadAsync(Ct);

        Assert.Equal(saved.NotesFolder, loaded.NotesFolder);
        Assert.Equal(saved.AttachmentsFolderName, loaded.AttachmentsFolderName);
        Assert.Equal(saved.Theme, loaded.Theme);
        Assert.Equal(saved.DefaultColor, loaded.DefaultColor);
        Assert.Equal(saved.DefaultWidth, loaded.DefaultWidth);
        Assert.Equal(saved.DefaultHeight, loaded.DefaultHeight);
        Assert.Equal(saved.ShowStatusBar, loaded.ShowStatusBar);
        Assert.Equal(saved.RestoreAfterShowDesktop, loaded.RestoreAfterShowDesktop);
        Assert.Equal(saved.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(saved.MinimizeToTrayOnClose, loaded.MinimizeToTrayOnClose);
        Assert.Equal(saved.ShowTrayIcon, loaded.ShowTrayIcon);
        Assert.Equal(saved.SingleClickTrayAction, loaded.SingleClickTrayAction);
        Assert.Equal(saved.GlobalQuickCaptureHotkey, loaded.GlobalQuickCaptureHotkey);
        Assert.Equal(saved.GlobalShowAllHotkey, loaded.GlobalShowAllHotkey);
        Assert.Equal(saved.AutoSaveDelayMs, loaded.AutoSaveDelayMs);
        Assert.Equal(saved.SearchDebounceMs, loaded.SearchDebounceMs);
        Assert.Equal(saved.TrashRetentionDays, loaded.TrashRetentionDays);
        Assert.Equal(saved.EnableAnimations, loaded.EnableAnimations);
        Assert.Equal(saved.MaxOpenWindows, loaded.MaxOpenWindows);
        Assert.Equal(saved.LogLevel, loaded.LogLevel);
    }

    [Fact]
    public async Task 颜色写成camelCase字符串而不是数字()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await store.SaveAsync(new AppSettings { DefaultColor = NoteColor.Orange }, Ct);

        // §8.2 的示例是 "defaultColor": "orange"。写成数字 5 的话，用户手改这个文件时
        // 根本无从下手，v1 就是栽在这里。
        Assert.Contains("\"defaultColor\": \"orange\"", await File.ReadAllTextAsync(paths.SettingsFile, Ct), StringComparison.Ordinal);
    }

    // ---- 磁盘形态 ----

    [Fact]
    public async Task 中文路径不被转义成转义序列()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await store.SaveAsync(new AppSettings { NotesFolder = @"D:\笔记" }, Ct);
        string json = await File.ReadAllTextAsync(paths.SettingsFile, Ct);

        // §8.4：默认编码器会写成 D:\u7B14\u8BB0，用户打开会以为文件坏了。
        Assert.Contains(@"D:\\笔记", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u7B14", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写出缩进JSON并以换行结尾()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await store.SaveAsync(new AppSettings(), Ct);
        string json = await File.ReadAllTextAsync(paths.SettingsFile, Ct);

        // §8.4 要求 WriteIndented：用户可能要手改。
        Assert.Contains("\n  \"version\": 1", json, StringComparison.Ordinal);

        // 没有结尾换行的文本文件在不少编辑器里会显示成「最后一行被吞了」。
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不残留临时文件()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await store.SaveAsync(new AppSettings(), Ct);

        Assert.Empty(Directory.GetFiles(local.Path, "*" + AtomicFileWriter.TempSuffix));
        Assert.True(File.Exists(paths.SettingsFile));
    }

    // ---- 越界值钳制（§9.3） ----

    [Theory]
    [InlineData(50, 300)]
    [InlineData(0, 300)]
    [InlineData(-100, 300)]
    [InlineData(9999, 800)]
    [InlineData(300, 300)]
    [InlineData(800, 800)]
    [InlineData(500, 500)]
    public async Task 自动保存去抖时长被钳进合法区间(int written, int expected)
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            $$"""{ "version": 1, "autoSaveDelayMs": {{written}} }""",
            Ct);

        Assert.Equal(expected, (await store.LoadAsync(Ct)).AutoSaveDelayMs);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(99, 100)]
    [InlineData(501, 500)]
    [InlineData(5000, 500)]
    [InlineData(150, 150)]
    public async Task 搜索去抖时长被钳进合法区间(int written, int expected)
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            $$"""{ "version": 1, "searchDebounceMs": {{written}} }""",
            Ct);

        Assert.Equal(expected, (await store.LoadAsync(Ct)).SearchDebounceMs);
    }

    [Fact]
    public async Task 保存时也钳制并就地改到传入的对象上()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);

        var settings = new AppSettings { AutoSaveDelayMs = 50 };
        await store.SaveAsync(settings, Ct);

        // 不就地改的话，设置窗口会显示 50 而实际生效 300，用户只会觉得「改了没用」。
        Assert.Equal(300, settings.AutoSaveDelayMs);
        Assert.Equal(300, (await store.LoadAsync(Ct)).AutoSaveDelayMs);
    }

    [Theory]
    [InlineData(@"..\..\Windows", "attachments")]
    [InlineData("a/b", "attachments")]
    [InlineData("a\\b", "attachments")]
    [InlineData("C:", "attachments")]
    [InlineData("..", "attachments")]
    [InlineData(".", "attachments")]
    [InlineData("", "attachments")]
    [InlineData("files", "files")]
    public async Task 附件目录名必须是安全的相对目录名(string written, string expected)
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            JsonSerializer.Serialize(new { version = 1, attachmentsFolderName = written }),
            Ct);

        // 这个值会被拼成 {笔记目录}\{附件目录名}（§6.1），"..\..\Windows" 会让附件
        // 跑到笔记目录外面去。文件是用户可改、也可能被同步过来的，属于系统边界。
        Assert.Equal(expected, (await store.LoadAsync(Ct)).AttachmentsFolderName);
    }

    // ---- 坏文件 ----

    [Fact]
    public async Task 内容不是JSON时改名备份并用默认值()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.SettingsFile, "这不是 JSON", Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(500, settings.AutoSaveDelayMs);
        Assert.Equal(SettingsLoadProblemKind.Corrupt, store.LastProblem?.Kind);

        string backup = Assert.IsType<string>(store.LastProblem?.BackupPath);

        // 原文件必须被挪走，否则下次启动又会读到同一份坏文件。
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(backup));
        Assert.Equal("这不是 JSON", await File.ReadAllTextAsync(backup, Ct));
    }

    [Fact]
    public async Task 字段类型不符时按损坏处理()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        // 版本号读得出来，所以「来自更新版本」那条路径不会命中，
        // 接下来反序列化时才炸——这正是要区分开的两条路径。
        await File.WriteAllTextAsync(paths.SettingsFile, """{ "version": 1, "notesFolder": { "x": 1 } }""", Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal("", settings.NotesFolder);
        Assert.Equal(SettingsLoadProblemKind.Corrupt, store.LastProblem?.Kind);
    }

    [Fact]
    public async Task 版本高于当前时不解析直接备份()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        // 这个字段在本版本里不存在，若先反序列化再判版本，会走到「损坏」那条路上去，
        // 把「请换个版本的程序」这个真正的出路盖掉。
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "version": 2, "somethingFromTheFuture": { "deep": [1, 2, 3] } }""",
            Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(500, settings.AutoSaveDelayMs);
        Assert.Equal(SettingsLoadProblemKind.NewerVersion, store.LastProblem?.Kind);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(store.LastProblem!.BackupPath));
    }

    [Fact]
    public async Task 缺版本号时当作第一版正常解析()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.SettingsFile, """{ "autoSaveDelayMs": 600 }""", Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(600, settings.AutoSaveDelayMs);
        Assert.Null(store.LastProblem);
    }

    [Fact]
    public async Task 认识的字段缺失时用默认值而不是零()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.SettingsFile, """{ "version": 1 }""", Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        // §8.4：新增字段必须有默认值，老配置文件读进来后用默认值。
        Assert.Equal(360, settings.DefaultWidth);
        Assert.Equal(420, settings.DefaultHeight);
        Assert.True(settings.ShowStatusBar);
        Assert.True(settings.MinimizeToTrayOnClose);
        Assert.Equal("toggleManager", settings.SingleClickTrayAction);
        Assert.Equal(30, settings.TrashRetentionDays);
    }

    [Fact]
    public async Task 不认识的字段被忽略且不报错()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        // §8.4：删除字段要「保持读入但忽略」。降级回旧版本时会看到这种文件。
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "version": 1, "keepNotesVisibleOnShowDesktop": true, "autoSaveDelayMs": 600 }""",
            Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(600, settings.AutoSaveDelayMs);
        Assert.Null(store.LastProblem);
    }

    [Fact]
    public async Task 忽略的字段下次写回时自然消失()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "version": 1, "keepNotesVisibleOnShowDesktop": true }""",
            Ct);

        await store.SaveAsync(await store.LoadAsync(Ct), Ct);

        Assert.DoesNotContain(
            "keepNotesVisibleOnShowDesktop",
            await File.ReadAllTextAsync(paths.SettingsFile, Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 内容就是字面量null时用默认值()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.SettingsFile, "null", Ct);

        Assert.Equal(500, (await store.LoadAsync(Ct)).AutoSaveDelayMs);
    }

    [Fact]
    public async Task 允许注释与尾随逗号()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        // 用户手改这个文件是设计内的用法（§8.4 要求缩进输出就是为了这个）。
        // 为一句注释把整份配置当损坏文件挪走，才是真正的伤害。
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              // 我的笔记放在 D 盘
              "version": 1,
              "autoSaveDelayMs": 600,
            }
            """,
            Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(600, settings.AutoSaveDelayMs);
        Assert.Null(store.LastProblem);
    }

    [Fact]
    public async Task 带BOM的文件也能读()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        // 记事本另存为 UTF-8 会加 BOM。BOM 若不吃掉，第一个字符就是 \uFEFF，
        // JsonNode.Parse 会直接失败，于是一份完好的配置被判成损坏。
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "version": 1, "autoSaveDelayMs": 600 }""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Ct);

        AppSettings settings = await store.LoadAsync(Ct);

        Assert.Equal(600, settings.AutoSaveDelayMs);
        Assert.Null(store.LastProblem);
    }

    [Fact]
    public async Task 连续两次损坏不再互相撞名()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.SettingsFile, "坏的", Ct);
        await store.LoadAsync(Ct);

        await File.WriteAllTextAsync(paths.SettingsFile, "又坏了", Ct);
        await store.LoadAsync(Ct);

        // 时钟没有推进，两次的时间戳完全相同。用覆盖式改名的话第二次会抛
        // 「目标已存在」，于是第一次备份之后再也备不成——而那正是最该留现场的时候。
        Assert.Equal(2, Directory.GetFiles(local.Path, "settings.json.corrupt-*").Length);
    }

    // ---- 辅助 ----

    private static (JsonSettingsStore Store, AppPaths Paths) CreateStore(TempDirectory local)
    {
        var paths = new AppPaths(local.Path);
        var clock = new FakeClock();

        // 重试间隔传空集合：集成测试不该为了一次重试白等一秒。
        var writer = new AtomicFileWriter(clock, retryDelaysMilliseconds: []);

        return (new JsonSettingsStore(paths, clock, writer, NullLogger<JsonSettingsStore>.Instance), paths);
    }
}
