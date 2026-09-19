using System.IO;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Events;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Watching;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Watching;

/// <summary>
/// <see cref="FileSystemWatcherAdapter"/> 的集成测试（§10.1、§10.4、§21.3）。
/// </summary>
/// <remarks>
/// <para>
/// 这里的被测对象是<strong>真的 Windows <c>FileSystemWatcher</c></strong>：单元测试替得掉
/// 「我们怎么处理事件」，替不掉「Windows 到底会不会报这个事件、报的是哪一种」。
/// 而后者正是本类的全部内容——把 <c>Renamed</c> 与「删除 + 新建」区分开、
/// 把不该收的路径挡在外面，都依赖真实事件的长相。
/// </para>
/// <para>
/// <strong>一律轮询等待，绝不用固定 <c>Sleep</c></strong>（§21.3）：事件在别的线程上到达，
/// 延迟没有上界，而每条用例各自用一段「稍等一会儿」来碰运气的话，
/// 它们会在 CI 上轮流飘红。见 <see cref="Waiting"/>。
/// </para>
/// <para>
/// 本类不打注释里那种「先记一笔再断言」的桩：事件本来就乱序、还可能重复
/// （写一个文件常常同时触发 <c>Changed</c> 与 <c>Size</c>），所以断言的都是
/// <strong>「某种事件出现过，且它带着正确的路径」</strong>，而不是「第几条是什么」。
/// </para>
/// </remarks>
public sealed class FileSystemWatcherAdapterTests
{
    // ---- 五种事件各报各的 ----

    [Fact]
    public async Task 外部新建文件_报新建事件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        using WatcherHarness harness = CreateHarness(local, notes);

        string path = notes.Combine("新来的.md");
        File.WriteAllText(path, "内容");

        FileWatchChange change = await harness.NextAsync(FileWatchChangeKind.Created);

        Assert.Equal(path, change.Path, ignoreCase: true);
    }

    [Fact]
    public async Task 外部修改文件_报修改事件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, "旧内容");

        using WatcherHarness harness = CreateHarness(local, notes);
        File.WriteAllText(path, "新内容");

        FileWatchChange change = await harness.NextAsync(FileWatchChangeKind.Changed);

        Assert.Equal(path, change.Path, ignoreCase: true);
    }

    [Fact]
    public async Task 外部删除文件_报删除事件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("要没的.md");
        File.WriteAllText(path, "内容");

        using WatcherHarness harness = CreateHarness(local, notes);
        File.Delete(path);

        FileWatchChange change = await harness.NextAsync(FileWatchChangeKind.Deleted);

        Assert.Equal(path, change.Path, ignoreCase: true);
    }

    [Fact]
    public async Task 外部重命名_报重命名事件并带上旧路径()
    {
        // 这条是 §10.1 特别点出来的：重命名若退化成「删除 + 新建」，
        // 外部改个文件名就会让那张便签消失又冒出一张新的——两张都开着窗口的时候尤其难看。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string before = notes.Combine("旧名字.md");
        string after = notes.Combine("新名字.md");
        File.WriteAllText(before, "内容");

        using WatcherHarness harness = CreateHarness(local, notes);
        File.Move(before, after);

        FileWatchChange change = await harness.NextAsync(FileWatchChangeKind.Renamed);

        Assert.Equal(after, change.Path, ignoreCase: true);
        Assert.Equal(before, change.OldPath, ignoreCase: true);
    }

    [Fact]
    public async Task 子目录里的md也会报()
    {
        // IncludeSubdirectories 为假的话，用户把便签分门别类放进子目录之后，
        // 那些文件的改动就再也感知不到了——而扫描器（EnumerateNoteFiles）是认子目录的，
        // 两边不一致会让「重启之后才看得见」这种最难受的缺陷。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string folder = notes.CreateDirectory("工作");

        using WatcherHarness harness = CreateHarness(local, notes);
        string path = Path.Combine(folder, "周报.md");
        File.WriteAllText(path, "内容");

        FileWatchChange change = await harness.NextAsync(FileWatchChangeKind.Created);

        Assert.Equal(path, change.Path, ignoreCase: true);
    }

    // ---- 不该收的不收（§5.7、§10.1）----
    //
    // 这两条要断言的是「什么都没发生」。直接等一段固定时间再断言「列表是空的」是
    // 最容易写、也最容易飘红的一种：机器一慢就假绿（事件迟到），机器一快就假红。
    //
    // 改用一条**对照事件**：先动那个不该报的文件，再动一个该报的普通文件，
    // 等对照事件到达。同一个 FileSystemWatcher 实例的事件是有序的，
    // 于是对照事件到了就说明前一个事件（若有）也早该到了。

    [Fact]
    public async Task 点目录里的md不报()
    {
        // .obsidian 之类是别的工具的元数据。收进来的话，用户改一下 Obsidian 的配置，
        // 便签列表里就会冒出一张重启后又不存在的幽灵便签。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string hidden = notes.CreateDirectory(".obsidian");

        using WatcherHarness harness = CreateHarness(local, notes);
        File.WriteAllText(Path.Combine(hidden, "配置.md"), "{}");

        await harness.RaiseControlEventAsync();

        Assert.DoesNotContain(
            harness.Events, change => change.Path.Contains(".obsidian", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 附件目录里的md不报()
    {
        // attachments 是用户长期放图片的目录（§7.x）。扫描器跳过它，监听器也必须跳过——
        // 判据不一致的话，「扫描时看不到、监听时看得到」会让便签列表里出现
        // 一张下次启动就消失的便签。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string attachments = notes.CreateDirectory("attachments");

        using WatcherHarness harness = CreateHarness(local, notes);
        File.WriteAllText(Path.Combine(attachments, "说明.md"), "附件说明");

        await harness.RaiseControlEventAsync();

        Assert.DoesNotContain(
            harness.Events, change => change.Path.Contains("attachments", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task md之外的扩展名不报()
    {
        // Filter 就是干这个的，但它是 FileSystemWatcher 的属性而不是我们的代码——
        // 写错了（比如 Filter 留空）不会有编译错误，只会在用户的目录里多出一堆
        // 不该管的通知，而那些通知到了上层才发现「这个文件根本不是便签」。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        using WatcherHarness harness = CreateHarness(local, notes);
        File.WriteAllText(notes.Combine("笔记.txt"), "不是便签");

        await harness.RaiseControlEventAsync();

        Assert.DoesNotContain(
            harness.Events, change => change.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 启停（§10.4 的重建就是 Stop + Start）----

    [Fact]
    public async Task 重复Start_同一个改动只报一次()
    {
        // Start 被调两次是很正常的：启动路径调一次，笔记目录后来才选定时又调一次，
        // 缓冲区溢出之后还会 Stop + Start 各一次（§10.4）。不幂等的话，
        // 每多调一次就多一个 FileSystemWatcher 在同一个目录上，同一个改动报两遍。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        using WatcherHarness harness = CreateHarness(local, notes);

        harness.Adapter.Start();
        harness.Adapter.Start();

        string first = notes.Combine("第一张.md");
        File.WriteAllText(first, "内容");
        await harness.NextAsync(FileWatchChangeKind.Created);

        // 对照事件：先等第二个文件的事件到达，再回头数第一个被报了几次。
        // 直接在这里断言的话，重复的那一条很可能还在路上。
        string second = notes.Combine("第二张.md");
        File.WriteAllText(second, "内容");
        await harness.WaitForPathAsync(second);

        // 只数**新建**这一类，不数这个路径上的全部事件：一次 WriteAllText 在 Windows 上
        // 本来就常常报两条（先 Created 再 Changed），按路径数会把操作系统的正常行为
        // 当成缺陷。而「同一个改动被两个 watcher 各报一遍」多出来的正是一条 Created。
        Assert.Equal(1, harness.Events.Count(change =>
            change.Kind == FileWatchChangeKind.Created
            && string.Equals(change.Path, first, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Stop之后不再上报_再Start之后又能收到()
    {
        // §10.4 的「重建」就是这个形状：Stop 把内部那个 FileSystemWatcher 整个丢掉，
        // Start 再造一个。丢掉之后旧实例不能再往外报（否则重建一次就多一份幽灵订阅），
        // 而再造出来的那个必须真的在工作——只 Stop 不 Start 的话监听就永远哑了。
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        using WatcherHarness harness = CreateHarness(local, notes);

        string duringStop = notes.Combine("停着的时候.md");
        harness.Adapter.Stop();
        File.WriteAllText(duringStop, "内容");

        harness.Adapter.Start();
        string afterStart = notes.Combine("重新开始之后.md");
        File.WriteAllText(afterStart, "内容");

        await harness.WaitForPathAsync(afterStart);

        // 停着的时候那个文件新建时，没有任何一个监听器活着；重启出来的那个
        // 只从它自己开始的那一刻记录，因此不会把旧账翻出来。
        Assert.DoesNotContain(
            harness.Events, change => string.Equals(change.Path, duringStop, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 尚未选定笔记目录_Start是空操作而不抛异常()
    {
        // 首次运行还没走完向导（§8.6）。那是启动顺序里的正常状态，不是错误：
        // 为此抛异常会让程序连启动都启动不了，而用户手上明明什么都还没做错。
        using var local = new TempDirectory();
        using WatcherHarness harness = CreateHarness(local, notes: null);

        harness.Adapter.Start();
        harness.Adapter.Start();
        harness.Adapter.Stop();
    }

    // ---- 辅助 ----

    private static WatcherHarness CreateHarness(TempDirectory local, TempDirectory? notes)
    {
        var paths = new AppPaths(local.Path);

        if (notes is not null)
        {
            paths.SetNotesFolder(notes.Path);
        }

        // 重试等待一律关掉：这里要验的是事件，不是「锁住之后等了三下」。
        var repository = new MarkdownNoteRepository(
            paths,
            new FakeClock(),
            new AtomicFileWriter(new FakeClock(), retryDelaysMilliseconds: []),
            NullLogger<MarkdownNoteRepository>.Instance,
            lockRetryDelaysMilliseconds: []);

        return new WatcherHarness(paths, repository, notes?.Path);
    }

    /// <summary>把适配器的回调攒起来，让用例能按种类或路径去等。</summary>
    /// <remarks>
    /// 回调在 <strong>线程池线程</strong>上跑（<see cref="IFileWatcher.FileChanged"/> 的约定），
    /// 所以那个列表必须上锁，读的那一侧也要拿一份快照。
    /// </remarks>
    private sealed class WatcherHarness : IDisposable
    {
        private readonly List<FileWatchChange> _events = [];
        private readonly object _gate = new();
        private readonly string? _notesRoot;

        public WatcherHarness(IAppPaths paths, INoteRepository repository, string? notesRoot)
        {
            _notesRoot = notesRoot;

            Adapter = new FileSystemWatcherAdapter(
                paths, repository, NullLogger<FileSystemWatcherAdapter>.Instance);

            Adapter.FileChanged += (_, change) =>
            {
                lock (_gate)
                {
                    _events.Add(change);
                }
            };

            Adapter.Start();
        }

        public FileSystemWatcherAdapter Adapter { get; }

        /// <summary>此刻已经收到的事件快照。</summary>
        public IReadOnlyList<FileWatchChange> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        /// <summary>等到某一类事件出现，返回第一条。</summary>
        public async Task<FileWatchChange> NextAsync(FileWatchChangeKind kind)
        {
            await Waiting.WaitUntilAsync(() => Events.Any(change => change.Kind == kind));

            return Events.First(change => change.Kind == kind);
        }

        /// <summary>等到某个路径出现在事件里。</summary>
        public Task WaitForPathAsync(string path) =>
            Waiting.WaitUntilAsync(() => Events.Any(
                change => string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// 在笔记目录里造一个普通便签文件并等它的事件到达。
        /// </summary>
        /// <remarks>
        /// 用来给「什么都没发生」那几条断言定一个顺序上的界：同一个
        /// <c>FileSystemWatcher</c> 实例的事件是按发生顺序投递的，对照事件既然到了，
        /// 那在它之前动过的那个文件（如果会被上报）也早该到了。
        /// 比等一段固定时间既快又不会飘。
        /// </remarks>
        public Task RaiseControlEventAsync()
        {
            if (_notesRoot is null)
            {
                throw new InvalidOperationException("本套装配没有笔记目录，造不出对照事件。");
            }

            string control = System.IO.Path.Combine(_notesRoot, $"对照-{Guid.NewGuid():N}.md");
            File.WriteAllText(control, "对照");

            return WaitForPathAsync(control);
        }

        public void Dispose() => Adapter.Dispose();
    }
}
