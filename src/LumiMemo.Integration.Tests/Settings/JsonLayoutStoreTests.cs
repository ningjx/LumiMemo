using System.IO;
using System.Text.Json;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Settings;

/// <summary>
/// <see cref="JsonLayoutStore"/> 的集成测试（§8.3、§8.5）。
/// </summary>
/// <remarks>
/// 两个重点：磁盘上的<strong>表名与键名</strong>（用户能看见、v1 在这里错过），
/// 以及<strong>什么时候真写盘</strong>（§8.5 的节流全靠「不脏就不写」这一条兜住）。
/// </remarks>
public sealed class JsonLayoutStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid NoteId = Guid.Parse("3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33");

    // ---- 首屏 ----

    [Fact]
    public async Task 文件不存在时没有任何布局且不创建文件()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await store.LoadAsync(Ct);

        Assert.Empty(store.All);
        Assert.False(File.Exists(paths.LayoutFile));
    }

    [Fact]
    public async Task 未载入就读布局直接抛异常()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);

        // 启动序列漏了一步的话，返回空集合会让「所有窗口位置都变成默认值」
        // 看起来像是布局文件坏了，排查起来要多绕一大圈。
        Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(NoteId));
        Assert.Throws<InvalidOperationException>(() => store.TryGet(NoteId));
        Assert.Throws<InvalidOperationException>(() => _ = store.All);
    }

    // ---- 新建 ----

    [Fact]
    public async Task 新建条目用默认尺寸且位置留待调用方接管()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.DefaultWidth = 400;
        store.DefaultHeight = 500;

        NoteLayout layout = store.GetOrCreate(NoteId);

        Assert.Equal(400, layout.Width);
        Assert.Equal(500, layout.Height);
        Assert.Equal(500, layout.ExpandedHeight);
        Assert.Equal(0, layout.X);
        Assert.Equal(0, layout.Y);

        // §8.3：未在 layout 中出现的便签，isOpen 默认为 true。
        Assert.True(layout.IsOpen);
    }

    [Fact]
    public async Task 重复取同一条返回同一个对象()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);
        await store.LoadAsync(Ct);

        // 返回副本的话，调用方改了折叠状态、存储里那份还是旧的，
        // 于是「界面上折起来了，重启后又展开」。
        Assert.Same(store.GetOrCreate(NoteId), store.GetOrCreate(NoteId));
    }

    [Fact]
    public async Task 取不存在的布局返回null且不产生副作用()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);
        await store.LoadAsync(Ct);

        Assert.Null(store.TryGet(NoteId));
        Assert.Empty(store.All);
    }

    // ---- 落盘时机（§8.5） ----

    [Fact]
    public async Task 标记脏本身不写盘()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.GetOrCreate(NoteId);
        store.MarkDirty();

        // §8.5：拖动窗口期间绝不写磁盘。触发点只置标志，写盘由节流后的 FlushAsync 统一做。
        Assert.False(File.Exists(paths.LayoutFile));
    }

    [Fact]
    public async Task 落盘后不脏再落盘时文件一个字节都不动()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.GetOrCreate(NoteId);
        await store.FlushAsync(Ct);

        DateTime firstWrite = File.GetLastWriteTimeUtc(paths.LayoutFile);

        await store.FlushAsync(Ct);

        // 不脏也照写的话，「启动 → 退出」什么都没做的会话会平白改掉这个文件的时间，
        // 干扰用户自己的备份与同步工具，也让「文件什么时候变的」这条排查线索失真。
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(paths.LayoutFile));
    }

    [Fact]
    public async Task 落盘失败不抛异常()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.GetOrCreate(NoteId);

        // 把 layout.json 这个路径占成一个目录，写入必定失败。
        Directory.CreateDirectory(paths.LayoutFile);

        // §8.5：布局写失败不打断用户操作。布局是设备状态，丢了顶多窗口位置回到默认。
        await store.FlushAsync(Ct);
    }

    // ---- 磁盘形态 ----

    [Fact]
    public async Task 表名是notes而不是windows_且条目里不重复存id()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        NoteLayout layout = store.GetOrCreate(NoteId);
        layout.X = 1260;
        layout.Y = 320;
        layout.Width = 380;
        layout.Height = 460;
        layout.ExpandedHeight = 460;
        layout.DisplayId = @"\\?\DISPLAY#DEL41A6#5&2b1c3d4e&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        layout.Dpi = 144;

        await store.FlushAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.LayoutFile, Ct));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("notes", out JsonElement notes));

        // v1 的表名是 windows，v2 改成 notes（§8.3）。
        Assert.False(root.TryGetProperty("windows", out _));

        JsonElement entry = notes.GetProperty("3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33");

        // 键就是 id，条目里再存一遍等于给「键与值不一致」留后门。
        Assert.False(entry.TryGetProperty("noteId", out _));

        Assert.Equal(1260, entry.GetProperty("x").GetDouble());
        Assert.Equal(320, entry.GetProperty("y").GetDouble());
        Assert.Equal(380, entry.GetProperty("width").GetDouble());
        Assert.Equal(460, entry.GetProperty("height").GetDouble());
        Assert.Equal(460, entry.GetProperty("expandedHeight").GetDouble());
        Assert.Equal(144u, entry.GetProperty("dpi").GetUInt32());
        Assert.True(entry.GetProperty("isOpen").GetBoolean());
        Assert.False(entry.GetProperty("isCollapsed").GetBoolean());
        Assert.False(entry.GetProperty("isTopMost").GetBoolean());
        Assert.False(entry.GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task 坐标以double无损往返()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        NoteLayout layout = store.GetOrCreate(NoteId);
        layout.X = -1279.5;
        layout.Y = 0.125;
        layout.Width = 360.75;
        layout.Height = 419.375;
        layout.ExpandedHeight = 419.1000000000001;

        await store.FlushAsync(Ct);
        var (reloaded, _) = CreateStore(local);
        await reloaded.LoadAsync(Ct);

        NoteLayout roundTripped = reloaded.TryGet(NoteId)!;

        // 存成整数像素就会丢精度，而「用户拖到哪里就是哪里」是这一整块的核心承诺。
        Assert.Equal(-1279.5, roundTripped.X);
        Assert.Equal(0.125, roundTripped.Y);
        Assert.Equal(360.75, roundTripped.Width);
        Assert.Equal(419.375, roundTripped.Height);
        Assert.Equal(419.1000000000001, roundTripped.ExpandedHeight);
    }

    [Fact]
    public async Task 布局以物理像素保存_不做任何DPI换算()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        NoteLayout layout = store.GetOrCreate(NoteId);
        layout.X = 1920;
        layout.Width = 540;
        layout.Dpi = 144;

        await store.FlushAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.LayoutFile, Ct));
        JsonElement entry = document.RootElement.GetProperty("notes").GetProperty(NoteId.ToString("D"));

        // §8.3：存进去的就是物理像素。存储层在这里做一次「除以缩放」的换算，
        // 换屏恢复时就会再乘一次，尺寸被反复缩放。
        Assert.Equal(1920, entry.GetProperty("x").GetDouble());
        Assert.Equal(540, entry.GetProperty("width").GetDouble());
        Assert.Equal(144u, entry.GetProperty("dpi").GetUInt32());
    }

    [Fact]
    public async Task 写出显示器快照且不含工作区()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.GetOrCreate(NoteId);
        await store.FlushAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.LayoutFile, Ct));
        JsonElement displays = document.RootElement.GetProperty("displays");
        Assert.Equal(2, displays.EnumerateObject().Count());

        JsonElement secondary = displays.GetProperty(@"\\.\DISPLAY2");

        Assert.Equal("DELL U2720Q", secondary.GetProperty("friendlyName").GetString());
        Assert.Equal(144u, secondary.GetProperty("dpi").GetUInt32());

        JsonElement bounds = secondary.GetProperty("boundsPx");
        Assert.Equal(1920, bounds.GetProperty("x").GetDouble());
        Assert.Equal(0, bounds.GetProperty("y").GetDouble());
        Assert.Equal(2560, bounds.GetProperty("width").GetDouble());
        Assert.Equal(1440, bounds.GetProperty("height").GetDouble());

        // §8.3 的表里没有工作区：它随任务栏与副屏旋转而变，
        // 记下来的是那一刻的噪声，而恢复算法要的是「现在」的工作区。
        Assert.False(bounds.TryGetProperty("workAreaPx", out _));
    }

    [Fact]
    public async Task 每次落盘都刷新显示器快照()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);
        store.GetOrCreate(NoteId);
        await store.FlushAsync(Ct);

        var withSecondMonitor = new JsonLayoutStore(
            new AppPaths(local.Path),
            new FakeClock(),
            new AtomicFileWriter(new FakeClock(), retryDelaysMilliseconds: []),
            new RecordingDisplayProvider(),
            NullLogger<JsonLayoutStore>.Instance);

        await withSecondMonitor.LoadAsync(Ct);
        withSecondMonitor.MarkDirty();
        await withSecondMonitor.FlushAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.LayoutFile, Ct));

        // 显示器拔掉之后，那个设备路径不该继续留在文件里假装它还在。
        Assert.Empty(document.RootElement.GetProperty("displays").EnumerateObject());
    }

    [Fact]
    public async Task 不残留临时文件()
    {
        using var local = new TempDirectory();
        var (store, _) = CreateStore(local);
        await store.LoadAsync(Ct);

        store.GetOrCreate(NoteId);
        await store.FlushAsync(Ct);

        Assert.Empty(Directory.GetFiles(local.Path, "*" + AtomicFileWriter.TempSuffix));
    }

    // ---- 载入 ----

    [Fact]
    public async Task 折叠置顶锁定与打开状态都能往返()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);
        await store.LoadAsync(Ct);

        NoteLayout first = store.GetOrCreate(NoteId);
        first.IsCollapsed = true;
        first.IsTopMost = true;
        first.IsLocked = true;
        first.IsOpen = false;

        Guid other = Guid.NewGuid();
        store.GetOrCreate(other);

        await store.FlushAsync(Ct);

        var (reloaded, _) = CreateStore(local);
        await reloaded.LoadAsync(Ct);

        Assert.Equal(2, reloaded.All.Count);

        NoteLayout restored = reloaded.TryGet(NoteId)!;
        Assert.True(restored.IsCollapsed);
        Assert.True(restored.IsTopMost);
        Assert.True(restored.IsLocked);
        Assert.False(restored.IsOpen);
    }

    [Fact]
    public async Task 非法id的条目被跳过而不是让整份布局失效()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.LayoutFile,
            $$"""
            {
              "version": 1,
              "notes": {
                "not-a-guid": { "x": 1, "y": 1, "width": 300, "height": 300 },
                "{{NoteId:D}}": { "x": 100, "y": 100, "width": 300, "height": 300, "isOpen": false }
              }
            }
            """,
            Ct);

        await store.LoadAsync(Ct);

        // 一条坏键带走全部窗口位置，是这份文件最没必要付出的代价。
        Assert.Single(store.All);
        Assert.Equal(100, store.TryGet(NoteId)?.X);
    }

    [Fact]
    public async Task 条目缺少字段时用默认值()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.LayoutFile,
            $$"""{ "version": 1, "notes": { "{{NoteId:D}}": { "x": 10 } } }""",
            Ct);

        await store.LoadAsync(Ct);
        NoteLayout layout = store.TryGet(NoteId)!;

        // §8.4 的向后兼容规则：新增字段必须给默认值，老文件读进来不抛异常。
        Assert.Equal(10, layout.X);
        Assert.Equal(360, layout.Width);
        Assert.Equal(420, layout.Height);
        Assert.Equal(96u, layout.Dpi);
        Assert.True(layout.IsOpen);
    }

    [Fact]
    public async Task 缺version字段时当作第一版()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.LayoutFile,
            $$"""{ "notes": { "{{NoteId:D}}": { "x": 42, "width": 300, "height": 300 } } }""",
            Ct);

        await store.LoadAsync(Ct);

        Assert.Equal(42, store.TryGet(NoteId)?.X);
    }

    [Fact]
    public async Task 缺displays段时也能载入()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.LayoutFile,
            $$"""{ "version": 1, "notes": { "{{NoteId:D}}": { "x": 42 } } }""",
            Ct);

        await store.LoadAsync(Ct);

        Assert.Single(store.All);
    }

    [Fact]
    public async Task notes段为null时当作空布局()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.LayoutFile, """{ "version": 1, "notes": null }""", Ct);

        await store.LoadAsync(Ct);

        Assert.Empty(store.All);
    }

    // ---- 坏文件 ----

    [Fact]
    public async Task 内容不是JSON时改名备份并用空布局启动()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(paths.LayoutFile, "{{{ 坏的", Ct);

        await store.LoadAsync(Ct);

        Assert.Empty(store.All);
        Assert.False(File.Exists(paths.LayoutFile));
        Assert.Single(Directory.GetFiles(local.Path, "layout.json.corrupt-*"));
    }

    [Fact]
    public async Task 版本高于当前时改名备份并用空布局启动()
    {
        using var local = new TempDirectory();
        var (store, paths) = CreateStore(local);

        await File.WriteAllTextAsync(
            paths.LayoutFile,
            $$"""{ "version": 2, "notes": { "{{NoteId:D}}": { "x": 1 } } }""",
            Ct);

        await store.LoadAsync(Ct);

        Assert.Empty(store.All);
        Assert.Single(Directory.GetFiles(local.Path, "layout.json.corrupt-*"));
    }

    // ---- 辅助 ----

    private static (JsonLayoutStore Store, AppPaths Paths) CreateStore(TempDirectory local)
    {
        var paths = new AppPaths(local.Path);
        var clock = new FakeClock();

        return (
            new JsonLayoutStore(
                paths,
                clock,
                new AtomicFileWriter(clock, retryDelaysMilliseconds: []),
                RecordingDisplayProvider.PrimaryAndSecondary(),
                NullLogger<JsonLayoutStore>.Instance),
            paths);
    }
}
