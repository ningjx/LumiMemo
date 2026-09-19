using System.IO;
using System.Text;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Integration.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="MarkdownNoteRepository"/> 的集成测试（§5.5、§5.7、§5.9、§5.10、§11.2）。
/// </summary>
/// <remarks>
/// 这些用例打真实文件系统，但每一棵树都在自己的临时目录里（§21.3）。
/// 它们验证的是「整个存储层串起来之后」的行为，也就是解析器、序列化器、原子写入三者
/// 互相咬合处容易出错的地方——这些地方单测任何一个组件都看不见。
/// </remarks>
public sealed class MarkdownNoteRepositoryTests
{
    private static readonly Guid SampleId = Guid.Parse("6f1d0a2e-1111-2222-3333-444455556666");

    private static readonly DateTimeOffset SampleTime =
        new(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>取消令牌。xunit.v3 要求显式传递（xUnit1051），这里统一取当前测试的。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 启动扫描（§5.7） ----

    [Fact]
    public async Task 尚未选定笔记目录_返回空列表而不是报错()
    {
        using var local = new TempDirectory();
        var paths = new AppPaths(local.Path);

        // §8.6：首次运行还没选目录。这是合法状态，不该抛异常，
        // 也不该顺手在进程当前目录下建出什么东西来。
        IReadOnlyList<Note> notes = await CreateRepository(paths).LoadAllAsync(Ct);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task 扫描只认md文件_跳过点目录附件目录与临时文件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        File.WriteAllText(notes.Combine("便签.md"), CanonicalText(SampleId));
        File.WriteAllText(notes.Combine("说明.txt"), "不是便签");
        File.WriteAllText(
            Path.Combine(notes.CreateDirectory("工作"), "周报.md"),
            CanonicalText(Guid.NewGuid()));
        File.WriteAllText(
            Path.Combine(notes.CreateDirectory(".obsidian"), "配置.md"),
            CanonicalText(Guid.NewGuid()));
        File.WriteAllText(
            Path.Combine(notes.CreateDirectory("attachments"), "截图说明.md"),
            CanonicalText(Guid.NewGuid()));
        File.WriteAllText(
            notes.Combine("半截.md.20260919120000123-a1b2c3d4" + AtomicFileWriter.TempSuffix),
            CanonicalText(Guid.NewGuid()));

        IReadOnlyList<Note> loaded = await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct);

        // 点目录里是别的工具的元数据（.obsidian / .git / .lumimemo），附件目录是用户的图片，
        // 临时文件是上次崩溃的残骸——三者都不该变成便签，否则用户的侧栏会凭空多出一堆东西。
        Assert.Equal(
            ["便签.md", "周报.md"],
            loaded.Select(note => Path.GetFileName(note.FilePath)).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 没有FrontMatter的文件_正文一字不动并在前面补出FrontMatter()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("随笔.md");
        const string original = "# 随手记\r\n\r\n今天有点冷。\r\n";
        File.WriteAllText(path, original);

        IReadOnlyList<Note> loaded = await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct);

        Note note = Assert.Single(loaded);
        Assert.Equal(original, note.Content);

        // §5.5：文件里没有 id 就补一个，并且立刻写回——只补内存不落盘的话，
        // 下次启动这张便签又会换一个身份。
        string written = File.ReadAllText(path);
        Assert.StartsWith("---\r\n", written, StringComparison.Ordinal);
        Assert.Contains($"id: {note.Id:D}", written, StringComparison.Ordinal);
        Assert.EndsWith(original, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未闭合的FrontMatter_整份文件仍是正文且被原样保留()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("半截.md");

        // 只有开头的 --- 而没有结束的 ---（§5.10）。用户写的内容一个字都不该被丢弃，
        // 因此整份文件都是正文，补出来的 Front Matter 只能接在它前面。
        const string original = "---\r\n# 我随手打了三个减号\r\n正文\r\n";
        File.WriteAllText(path, original);

        Note note = Assert.Single(await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct));

        Assert.Equal(original, note.Content);
        Assert.EndsWith(original, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 零字节文件_当作空便签并补上id()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("空的.md");
        File.WriteAllText(path, string.Empty);

        Note note = Assert.Single(await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct));

        Assert.Equal(string.Empty, note.Content);
        Assert.Contains($"id: {note.Id:D}", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 扫描时清掉上次残留的临时文件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string orphan = notes.Combine("便签.md.20260919120000123-a1b2c3d4" + AtomicFileWriter.TempSuffix);
        File.WriteAllText(orphan, "上次写到一半的残骸");

        await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct);

        // 残骸留着会一直躺在用户目录里，而且下次扫描还得再判断一次。
        Assert.False(File.Exists(orphan));
    }

    // ---- id 补给与冲突（§5.5） ----

    [Fact]
    public async Task id冲突_按路径排序保留第一个_其余重新分配()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        // 刻意先写 b 再写 a：胜者必须由路径排序决定，而不是由写入顺序或枚举顺序决定。
        File.WriteAllText(notes.Combine("b-后写的.md"), CanonicalText(SampleId));
        File.WriteAllText(notes.Combine("a-先写的.md"), CanonicalText(SampleId));

        IReadOnlyList<Note> loaded = await CreateRepository(PathsFor(local, notes)).LoadAllAsync(Ct);

        Note winner = loaded.Single(note => Path.GetFileName(note.FilePath) == "a-先写的.md");
        Note loser = loaded.Single(note => Path.GetFileName(note.FilePath) == "b-后写的.md");

        // 按路径排序是确定的：同一台机器上跑多少次、同步工具碰几次文件，胜者都不会变。
        // 换成按修改时间，便签的身份就会在设备之间漂移。
        Assert.Equal(SampleId, winner.Id);
        Assert.NotEqual(SampleId, loser.Id);

        // 两人的 id 都必须在磁盘上落定，否则下次启动又要重算一遍。
        Assert.Contains($"id: {SampleId:D}", File.ReadAllText(winner.FilePath), StringComparison.Ordinal);
        Assert.Contains($"id: {loser.Id:D}", File.ReadAllText(loser.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrontMatter读不懂_先备份原文再修复()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("坏了.md");
        const string broken = "---\r\nid: [没有闭合\r\n---\r\n\r\n正文\r\n";
        File.WriteAllText(path, broken);

        var clock = new FakeClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        Note note = Assert.Single(await CreateRepository(PathsFor(local, notes), clock).LoadAllAsync(Ct));

        // §5.10：读不懂的 Front Matter 一律先留一份原样的给用户。
        // 只修不备份的话，那几行用户自己写的、我们不认识的东西就被静默抹掉了。
        string backup = $"{path}.20260304050607.bak";
        Assert.True(File.Exists(backup), "备份文件没有生成");
        Assert.Equal(broken, File.ReadAllText(backup));

        // 原文既然完好地留在了备份里，主文件就可以放心修：补上 id，且不再记「读不懂」以外的问题。
        Assert.Contains($"id: {note.Id:D}", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains(note.ParseIssues, issue => issue.Kind == NoteParseIssueKind.InvalidYaml);
    }

    [Fact]
    public async Task 备份文件的扩展名不是md_不会被当成便签扫进来()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        File.WriteAllText(notes.Combine("坏了.md"), "---\r\nid: [没有闭合\r\n---\r\n\r\n正文\r\n");

        AppPaths paths = PathsFor(local, notes);
        await CreateRepository(paths, new FakeClock()).LoadAllAsync(Ct);
        IReadOnlyList<Note> second = await CreateRepository(paths, new FakeClock()).LoadAllAsync(Ct);

        // 备份是「上一次启动的存档」，不该在第二次启动时变成一张真的便签。
        Assert.Single(second);
    }

    // ---- 保存（§5.9、§11.2） ----

    [Fact]
    public async Task 内容没有变化时_不写磁盘()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId));

        var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, marker);

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Note note = Assert.Single(await repository.LoadAllAsync(Ct));

        await repository.SaveAsync(note, Ct);

        // §5.9 的写前自检：读进来什么都没改就写回去，磁盘上必须一个字节都不动。
        // 用户打开便签看一眼再关掉，不该在 git 里留下任何痕迹。
        Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task 改了颜色再保存_行尾仍是CRLF且不补末尾换行()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");

        // 原文没有结尾换行：这是用户自己的排版习惯，写回时不能顺手补一个。
        File.WriteAllText(path, CanonicalText(SampleId, body: "正文没有结尾换行"));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Note note = Assert.Single(await repository.LoadAllAsync(Ct));
        note.Color = NoteColor.Blue;

        await repository.SaveAsync(note, Ct);

        string written = File.ReadAllText(path);

        Assert.Contains("color: blue", written, StringComparison.Ordinal);

        // 把成对的 CRLF 摘掉之后不该再剩下任何换行符，否则就是把用户的 LF 或单个 CR 混进来了。
        Assert.DoesNotContain(
            "\n",
            written.Replace("\r\n", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);

        Assert.EndsWith("正文没有结尾换行", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 改了颜色再保存_UTF16文件仍然是UTF16()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("宽字符.md");
        File.WriteAllBytes(
            path,
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(CanonicalText(SampleId))]);

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Note note = Assert.Single(await repository.LoadAllAsync(Ct));
        note.Color = NoteColor.Blue;

        await repository.SaveAsync(note, Ct);

        // §5.9：编码原样保留。若这里按 UTF-8 写回，Notepad 里打开就是满屏乱码，
        // 而用户根本不知道自己做了什么导致这个结果。
        byte[] raw = File.ReadAllBytes(path);
        Assert.Equal([0xFF, 0xFE], raw[..2]);
        Assert.Contains("color: blue", Encoding.Unicode.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知字段保存后仍在_且相对顺序不变()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("插件笔记.md");
        File.WriteAllText(
            path,
            "---\r\n"
            + "zebra: 1\r\n"
            + $"id: {SampleId:D}\r\n"
            + "alpha: two\r\n"
            + "---\r\n"
            + "\r\n正文\r\n");

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Note note = Assert.Single(await repository.LoadAllAsync(Ct));
        note.Color = NoteColor.Pink;

        await repository.SaveAsync(note, Ct);

        // §5.3：Obsidian 的插件字段我们看不懂，但必须原样还回去，
        // 否则用户装的那些插件会在便签被保存过一次之后集体失灵。
        string written = File.ReadAllText(path);
        int zebra = written.IndexOf("zebra: 1", StringComparison.Ordinal);
        int alpha = written.IndexOf("alpha: two", StringComparison.Ordinal);

        Assert.True(zebra >= 0 && alpha >= 0, "未知字段被丢掉了");
        Assert.True(zebra < alpha, "未知字段的相对顺序变了");
    }

    [Fact]
    public async Task 保存一张新建的便签_按给定路径落盘()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = NoteFileNameBuilder.Build("会议记录", SampleTime, SampleId, notes.Path, notes.Path);

        var note = new Note
        {
            Id = SampleId,
            FilePath = path,
            Content = "下周三复盘\r\n",
            Color = NoteColor.Green,
            CreatedAt = SampleTime,
            UpdatedAt = SampleTime,
        };

        await CreateRepository(PathsFor(local, notes)).SaveAsync(note, Ct);

        string written = File.ReadAllText(path);
        Assert.StartsWith("---\r\n", written, StringComparison.Ordinal);
        Assert.Contains("color: green", written, StringComparison.Ordinal);
        Assert.EndsWith("---\r\n\r\n下周三复盘\r\n", written, StringComparison.Ordinal);
    }

    // ---- 外部改动（§10.3） ----

    [Fact]
    public async Task 文件被外部改过_重新读取会同步内容且身份不变()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId, body: "旧正文\r\n"));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Assert.Single(await repository.LoadAllAsync(Ct));

        File.WriteAllText(path, CanonicalText(SampleId, color: "blue", body: "外部改过的正文\r\n"));

        Note? reloaded = await repository.ReloadAsync(path, Ct);

        Assert.NotNull(reloaded);
        Assert.Equal(SampleId, reloaded.Id);
        Assert.Equal("外部改过的正文\r\n", reloaded.Content);
        Assert.Equal(NoteColor.Blue, reloaded.Color);
    }

    [Fact]
    public async Task 外部把FrontMatter清空了_身份沿用上一次记住的id()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Assert.Single(await repository.LoadAllAsync(Ct));

        // 用户在别的编辑器里把 Front Matter 删掉了。他改的是内容，不是便签的身份：
        // 若这里换一个新 id，主窗口那张便签就成了「被删掉 + 新增一张」。
        File.WriteAllText(path, "只剩正文了\r\n");

        Note? reloaded = await repository.ReloadAsync(path, Ct);

        Assert.NotNull(reloaded);
        Assert.Equal(SampleId, reloaded.Id);
        Assert.Contains(reloaded.ParseIssues, issue => issue.Kind == NoteParseIssueKind.MissingId);
    }

    [Fact]
    public async Task 重新读取不回写文件()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Assert.Single(await repository.LoadAllAsync(Ct));

        File.WriteAllText(path, "只剩正文了\r\n");
        var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, marker);

        await repository.ReloadAsync(path, Ct);

        // 外部编辑是用户的动作，我们只同步内存。反过来立刻写回会和用户的编辑器抢文件，
        // 让他正在打的那行字消失（§10.3）。
        Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task 文件被外部删掉_重新读取返回null()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Assert.Single(await repository.LoadAllAsync(Ct));

        File.Delete(path);

        // null 的含义是「便签没了」，与「这一刻读不到」是两回事。
        Assert.Null(await repository.ReloadAsync(path, Ct));
    }

    [Fact]
    public async Task 文件被独占锁定_重新读取抛临时锁定异常而不是返回null()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();
        string path = notes.Combine("便签.md");
        File.WriteAllText(path, CanonicalText(SampleId));

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes));
        Note original = Assert.Single(await repository.LoadAllAsync(Ct));

        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // §5.10：重试三次仍读不出来时抛异常，让调用方保留内存里的旧内容。
            // 若这里返回 null，便签会从 NoteStore 里消失——用户看到的是「我的便签不见了」。
            var thrown = await Assert.ThrowsAsync<NoteTemporarilyLockedException>(
                () => repository.ReloadAsync(path, Ct));

            Assert.Contains(path, thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        // 锁一松开就能读到了，这正是「临时」这个词的含义。
        Note? reloaded = await repository.ReloadAsync(path, Ct);
        Assert.Equal(original.Id, reloaded?.Id);
    }

    // ---- 新建（§5.6、§3.3 流 3） ----

    [Fact]
    public async Task 新建的便签文件名是标题摘要加创建日期加短ID()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        Note note = await CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime))
            .CreateAsync(ct: Ct);

        // §5.6：{标题摘要}-{创建日期}-{短ID}.md。初始正文为空，标题派生结果就是「无标题」。
        string expected = $"无标题-20260919-{note.Id.ToString("N")[..8]}.md";

        Assert.Equal(expected, Path.GetFileName(note.FilePath));

        // 磁盘上真有且只有这一个文件。只断言返回值的话，一个「名字算对了但根本没写盘」
        // 的实现照样能过。
        Assert.Equal(expected, Path.GetFileName(Assert.Single(Directory.GetFiles(notes.Path))));
    }

    [Fact]
    public async Task 新建的文件是无BOM的UTF8以CRLF分行的FrontMatter加一个空行()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        Note note = await CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime))
            .CreateAsync(ct: Ct);

        byte[] bytes = await File.ReadAllBytesAsync(note.FilePath, Ct);

        // §5.9 的四者之一：编码。新文件的 BOM 有一半来自「读进来的文件长什么样」，
        // 全新文件没有那个来源，只能由第一次写盘自己定——错了就再也纠不回来。
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));

        // 其余三者（行尾 CRLF、正文前一个空行、末尾换行）与四个键的顺序一起钉在这里。
        // CanonicalText 的 body 传空串，得到的正是「空正文的全新便签」该有的样子。
        Assert.Equal(CanonicalText(note.Id, body: string.Empty), Encoding.UTF8.GetString(bytes));

        // 空标签表省略整个键，而不是写成 tags: []（§5.2）。多少用户会被那个空数组
        // 引着去手改，然后困惑于「为什么删了它又回来了」。
        Assert.DoesNotContain("tags", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 新建的便签马上就能被扫描读到()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime));
        Note created = await repository.CreateAsync(ct: Ct);

        // 「新建之后重开程序，它还在」是用户对便签最朴素的期待。
        // 写盘与解析两条路要能对上，光有写盘的用例看不出这一点。
        Note reloaded = Assert.Single(await repository.LoadAllAsync(Ct));

        Assert.Equal(created.Id, reloaded.Id);
        Assert.Equal(created.FilePath, reloaded.FilePath);
        Assert.Equal(string.Empty, reloaded.Content);
        Assert.Equal(created.Color, reloaded.Color);
    }

    [Fact]
    public async Task 不传颜色时用默认色_传了就用传的()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        MarkdownNoteRepository repository = CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime));

        Assert.Equal(NoteColor.Yellow, (await repository.CreateAsync(ct: Ct)).Color);

        // 默认色是设置项，运行期可以改（§8.2）。做成可写属性就是为了这样用。
        repository.DefaultColor = NoteColor.Purple;
        Assert.Equal(NoteColor.Purple, (await repository.CreateAsync(ct: Ct)).Color);

        Note blue = await repository.CreateAsync(NoteColor.Blue, ct: Ct);
        Assert.Equal(NoteColor.Blue, blue.Color);

        // 内存里的对象对了不算数：Front Matter 里那一行才是用户与别的工具看得见的。
        Assert.Contains(
            "color: blue",
            await File.ReadAllTextAsync(blue.FilePath, Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 传了目标文件夹就落在子目录里且目录被自动建出来()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        Note note = await CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime))
            .CreateAsync(targetFolder: "工作", ct: Ct);

        Assert.Equal(Path.Combine(notes.Path, "工作"), Path.GetDirectoryName(note.FilePath));
        Assert.True(File.Exists(note.FilePath));

        // 根目录下不该多出一份。
        Assert.Empty(Directory.GetFiles(notes.Path));
    }

    [Fact]
    public async Task 目标文件夹逃出笔记目录时退回到根目录()
    {
        using var local = new TempDirectory();
        using var notes = new TempDirectory();

        Note note = await CreateRepository(PathsFor(local, notes), new FakeClock(SampleTime))
            .CreateAsync(targetFolder: @"..\..\Windows", ct: Ct);

        // §19.4：不许写到笔记目录外面去。NoteFileNameBuilder 里那道校验只护住文件名，
        // 目录这一层得仓储自己拦——否则它会老老实实拼出
        // {笔记目录}\..\..\Windows\untitled-xxxx.md，然后真的写出去。
        Assert.True(NoteFileNameBuilder.IsInsideNotesRoot(note.FilePath, notes.Path));
        Assert.Single(Directory.GetFiles(notes.Path));
    }

    [Fact]
    public async Task 尚未选定笔记目录时拒绝新建且磁盘上什么都没建()
    {
        using var local = new TempDirectory();

        // §8.6：首次运行还没选目录。这是合法状态，但新建便签没有地方落。
        var paths = new AppPaths(local.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRepository(paths, new FakeClock(SampleTime)).CreateAsync(ct: Ct));

        Assert.Empty(Directory.GetFiles(local.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 笔记目录不存在时拒绝新建而不是凭空把目录造出来()
    {
        using var local = new TempDirectory();

        // 拔掉的移动盘就是这个样子：设置里还记着那个路径，目录已经没了。
        string missing = Path.Combine(local.Path, "已经拔掉的盘");
        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(missing);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRepository(paths, new FakeClock(SampleTime)).CreateAsync(ct: Ct));

        // AtomicFileWriter 写之前会 CreateDirectory。不在这一层拦住的话，目录会被凭空造出来，
        // 本次新建「成功」，而用户看不到自己原有的任何一张便签。
        Assert.False(Directory.Exists(missing));
    }

    // ---- 辅助 ----

    private static AppPaths PathsFor(TempDirectory local, TempDirectory notes)
    {
        var paths = new AppPaths(local.Path);
        paths.SetNotesFolder(notes.Path);

        return paths;
    }

    /// <summary>造一个「id、颜色、时间戳齐全」的标准文件正文。</summary>
    private static string CanonicalText(
        Guid id,
        string color = "yellow",
        string body = "正文\r\n") =>
        "---\r\n"
        + $"id: {id:D}\r\n"
        + $"color: {color}\r\n"
        + "createdAt: 2026-09-19T10:00:00+08:00\r\n"
        + "updatedAt: 2026-09-19T10:00:00+08:00\r\n"
        + "---\r\n"
        + "\r\n"
        + body;

    /// <summary>
    /// 造一个仓储。
    /// </summary>
    /// <remarks>
    /// 重试等待一律关掉：这些用例要验证的是「锁住之后会抛异常」，不是「等了三下」。
    /// 默认的 100/300/600 毫秒会让每条用例白等一秒。
    /// </remarks>
    private static MarkdownNoteRepository CreateRepository(IAppPaths paths, IClock? clock = null)
    {
        clock ??= new FakeClock();

        return new MarkdownNoteRepository(
            paths,
            clock,
            new AtomicFileWriter(clock, retryDelaysMilliseconds: []),
            NullLogger<MarkdownNoteRepository>.Instance,
            lockRetryDelaysMilliseconds: []);
    }
}
