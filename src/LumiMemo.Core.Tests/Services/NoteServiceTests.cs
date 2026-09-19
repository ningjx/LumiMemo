using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using LumiMemo.Core.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary>
/// <see cref="NoteService"/> 的单元测试：内存编排与开窗判断（§14.2）。
/// </summary>
/// <remarks>
/// 真正的磁盘行为在 <c>LumiMemo.Integration.Tests</c> 里用真实文件系统覆盖，
/// 这里只验本类的两件事：<strong>该写内存的时候写了内存</strong>，
/// 以及<strong>该落盘的时候把活交给了仓储</strong>。
/// </remarks>
public sealed class NoteServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================= 磁盘 → 内存 =================

    [Fact]
    public async Task 载入时_把仓储的便签放进内存并建好索引()
    {
        var harness = CreateHarness();
        Note first = NewNote("# 购物清单\n- 牛奶");
        Note second = NewNote("# 会议记录\n下周三");
        harness.Repository.NotesToLoad.AddRange([first, second]);

        await harness.Service.LoadAllAsync(Ct);

        Assert.Equal(2, harness.Store.Count);
        Assert.Same(first, harness.Store.TryGet(first.Id));
        Assert.Equal(1, harness.Repository.LoadCallCount);

        // 索引必须跟着建起来，否则管理器的搜索框一上来就是空的。
        Assert.Contains("牛奶", harness.Index.GetPlainText(first.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 载入时_清掉上一次留在内存里的便签()
    {
        // 切换笔记目录后必须重新载入（§8.6）。若只做 Add 不 Clear，
        // 上一个目录的便签会留在列表里，点开就是「文件不存在」。
        var harness = CreateHarness();
        harness.Store.Add(NewNote("上一个目录的便签", Guid.NewGuid()));

        harness.Repository.NotesToLoad.Add(NewNote("新目录的便签"));

        await harness.Service.LoadAllAsync(Ct);

        Assert.Equal(1, harness.Store.Count);
        Assert.Contains("新目录", harness.Store.Snapshot()[0].Content, StringComparison.Ordinal);
    }

    // ================= 编辑 → 内存 =================

    [Fact]
    public void 本地编辑_写回正文并更新修改时间()
    {
        var harness = CreateHarness();
        Note note = NewNote("旧内容");
        harness.Store.Add(note);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        harness.Service.ApplyLocalEdit(note, "新内容");

        Assert.Equal("新内容", note.Content);
        Assert.Equal(harness.Clock.Now, note.UpdatedAt);
        Assert.Contains("新内容", harness.Index.GetPlainText(note.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void 本地编辑_不写磁盘()
    {
        // 流 1 的前半段只碰内存（§3.3）。真正落盘由 AutoSaveService 去抖后调 SaveNoteAsync。
        // 这条断言是「每次按键都写盘」这个缺陷的拦路石。
        var harness = CreateHarness();
        Note note = NewNote("旧内容");

        harness.Service.ApplyLocalEdit(note, "新内容");

        Assert.Empty(harness.Repository.Saved);
    }

    // ================= 颜色与标签的编辑 =================

    [Fact]
    public void 改颜色_写回便签并更新修改时间()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        harness.Service.ApplyColorEdit(note, NoteColor.Blue);

        Assert.Equal(NoteColor.Blue, note.Color);
        Assert.Equal(harness.Clock.Now, note.UpdatedAt);
    }

    // 这里刻意没有一条「改颜色不动索引」的用例：索引的状态不随颜色变化，
    // 那份断言不管实现有没有多喊一次 OnNoteUpdated 都会过——它测不出东西。
    // 该由代码注释守住的事，别摆一条假装有覆盖的测试。

    [Fact]
    public void 改标签_写回并更新修改时间()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        harness.Service.ApplyTagsEdit(note, ["工作", "紧急"]);

        Assert.Equal(["工作", "紧急"], note.Tags);
        Assert.Equal(harness.Clock.Now, note.UpdatedAt);
    }

    [Fact]
    public void 改标签_按规则规范化去空去重()
    {
        // 调用方可以直接把用户打的字交进来（INoteService.ApplyTagsEdit 的约定）：
        // 「Note.Tags 里永远是规范形式」这条不变量由这里守住，
        // 而不是指望每一处调用都记得先自己跑一遍 TagRules。
        var harness = CreateHarness();
        Note note = NewNote("内容");

        harness.Service.ApplyTagsEdit(note, [" #Work ", "work", "to read", "   ", ""]);

        Assert.Equal(["Work", "to-read"], note.Tags);
    }

    [Fact]
    public void 改标签_原地换内容而不是换掉整个列表()
    {
        // Note.Tags 的引用已经散出去了（NoteListItem 直接把这份列表交到界面上）。
        // 换掉整个对象的话，那些引用会一直指向旧数据，界面上标签从此不再更新。
        var harness = CreateHarness();
        Note note = NewNote("内容");
        note.Tags.Add("旧标签");
        List<string> handedOut = note.Tags;

        harness.Service.ApplyTagsEdit(note, ["新标签"]);

        Assert.Same(handedOut, note.Tags);
        Assert.Equal(["新标签"], handedOut);
    }

    [Fact]
    public void 改标签_传空集合就是清空()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        note.Tags.Add("旧标签");

        harness.Service.ApplyTagsEdit(note, []);

        Assert.Empty(note.Tags);
    }

    [Fact]
    public void 改标签_刷新索引里的标签索引()
    {
        // SearchIndex 为标签单独存了一份 byTag。不刷的话，改完之后
        // 按新标签搜不到这张便签、按旧标签反而还搜得到（§12.1 的标签命中）。
        var harness = CreateHarness();
        Note note = NewNote("内容");
        note.Tags.Add("旧标签");
        harness.Store.Add(note);
        harness.Index.OnNoteAdded(note);

        Assert.Contains(note.Id, harness.Index.NotesWithTag("旧标签")!);

        harness.Service.ApplyTagsEdit(note, ["新标签"]);

        Assert.Contains(note.Id, harness.Index.NotesWithTag("新标签")!);

        // 旧标签下已经没有便签了，那一格整个消失（NotesWithTag 此时返回 null）。
        Assert.Null(harness.Index.NotesWithTag("旧标签"));
    }

    [Fact]
    public void 改标签_不写磁盘()
    {
        // 与本地编辑同理：落盘由调用方（管理器的 PersistEditAsync）显式发起。
        var harness = CreateHarness();
        Note note = NewNote("内容");

        harness.Service.ApplyTagsEdit(note, ["工作"]);

        Assert.Empty(harness.Repository.Saved);
    }

    // ================= 内存 → 磁盘 =================

    [Fact]
    public async Task 保存_把便签交给仓储()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        await harness.Service.SaveNoteAsync(note.Id);

        Assert.Same(note, Assert.Single(harness.Repository.Saved));
    }

    [Fact]
    public async Task 保存_便签已被删掉时静默返回而不是抛异常()
    {
        // 自动保存是按 id 排的队，用户完全可能在去抖那 500 毫秒里把便签删了。
        // 那不是错误，不该炸在定时器回调里。
        var harness = CreateHarness();

        await harness.Service.SaveNoteAsync(Guid.NewGuid());

        Assert.Empty(harness.Repository.Saved);
    }

    [Fact]
    public async Task 保存全部_把每张便签都交给仓储()
    {
        var harness = CreateHarness();
        harness.Store.Add(NewNote("第一张"));
        harness.Store.Add(NewNote("第二张"));

        await harness.Service.SaveAllAsync(Ct);

        Assert.Equal(2, harness.Repository.Saved.Count);
    }

    // ================= 删除与恢复 =================

    [Fact]
    public async Task 删除便签_走回收站而不是直接删文件()
    {
        // §7.1：删掉的便签必须还能捞回来。这里若直接 File.Delete，
        // 用户按一次 Delete 就永久丢了内容，而菜单上写着的是「移到回收站」。
        var harness = CreateHarness();
        Note note = NewNote("要删的");
        harness.Store.Add(note);

        await harness.Service.DeleteNoteAsync(note.Id);

        TrashEntry entry = Assert.Single(harness.Trash.Entries);
        Assert.Equal(note.Id, entry.NoteId);
        Assert.Equal(Path.GetFileName(note.FilePath), Path.GetFileName(entry.OriginalRelativePath));
    }

    [Fact]
    public async Task 删除便签_把它从内存与索引里摘掉()
    {
        // 不摘内存的话，管理器的列表里还留着一条，用户点开就会看到自己刚删掉的便签。
        var harness = CreateHarness();
        Note note = NewNote("# 会议记录");
        harness.Store.Add(note);
        harness.Index.OnNoteAdded(note);

        await harness.Service.DeleteNoteAsync(note.Id);

        Assert.Null(harness.Store.TryGet(note.Id));

        // 索引里也得干干净净，否则搜索框还能搜到一张已经不在列表上的便签。
        Assert.Empty(harness.Index.GetPlainText(note.Id));
    }

    [Fact]
    public async Task 删除便签_内存里没有这张便签时抛异常()
    {
        // 调用方拿着的 id 来自它自己那份列表。对不上说明那份列表已经过期，
        // 静默返回会让界面以为「删掉了」而回收站里什么都没有。
        var harness = CreateHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeleteNoteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task 删除便签_不碰布局记录()
    {
        // §7.3：按 id 索引的窗口位置在删除时不清除，恢复之后折叠状态、置顶、
        // 位置全都自己回来。删的时候顺手清掉的话，用户会以为「回收站只还回了内容」。
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);
        _ = harness.Service.OpenNote(note.Id);

        await harness.Service.DeleteNoteAsync(note.Id);

        Assert.True(harness.Layouts.TryGet(note.Id)!.IsOpen);
    }

    [Fact]
    public async Task 恢复便签_按id找到条目再交给回收站()
    {
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();
        TrashEntry entry = new()
        {
            TrashName = "20260101-000000-周报.md",
            OriginalRelativePath = "周报.md",
            NoteId = noteId,
            DeletedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Kind = TrashEntryKind.File,
        };

        harness.Trash.Entries.Add(entry);

        await harness.Service.RestoreFromTrashAsync(noteId, targetPath: null);

        // 传 null 表示「回原位」——具体落到哪个路径由存储层拿 OriginalRelativePath 解出来，
        // 这一层只负责把「按 id 找到的那一条」交下去。
        var restore = Assert.Single(harness.Trash.Restores);
        Assert.Same(entry, restore.Entry);
        Assert.Null(restore.Target);
    }

    [Fact]
    public async Task 恢复便签_把目标路径原样传给回收站()
    {
        // §7.3 的「恢复到笔记目录根」那一档就是不传原路径、改传一个新路径。
        var harness = CreateHarness();
        Guid noteId = Guid.NewGuid();
        harness.Trash.Entries.Add(new TrashEntry
        {
            TrashName = "20260101-000000-周报.md",
            OriginalRelativePath = "归档/周报.md",
            NoteId = noteId,
            DeletedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Kind = TrashEntryKind.File,
        });

        await harness.Service.RestoreFromTrashAsync(noteId, "周报.md");

        var restore = Assert.Single(harness.Trash.Restores);
        Assert.Equal("周报.md", restore.Target);
    }

    [Fact]
    public async Task 恢复便签_回收站里没有这个id时抛异常()
    {
        // 拿着的 id 可能在回收站被清空之后就失效了。静默成功会让界面以为
        // 「恢复好了」而列表里什么都不出现。
        var harness = CreateHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.RestoreFromTrashAsync(Guid.NewGuid(), targetPath: null));
    }

    // ================= 开窗判断 =================

    [Fact]
    public void 打开便签_返回请求并把IsOpen置为真()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        NoteOpenRequest? request = harness.Service.OpenNote(note.Id);

        Assert.NotNull(request);
        Assert.Same(note, request.Value.Note);
        Assert.True(request.Value.Layout.IsOpen);
    }

    [Fact]
    public void 打开从未出现过的便签_顺带建好它的布局条目()
    {
        // 一张刚放进目录、还没被打开过的便签走到这里才第一次拿到布局条目，
        // 而 §8.3 要求新条目必须落盘——否则下次启动无从知道它上次是开着的。
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        _ = harness.Service.OpenNote(note.Id);

        Assert.NotNull(harness.Layouts.TryGet(note.Id));
        Assert.True(harness.Timers.Last.IsRunning);
    }

    [Fact]
    public void 打开不存在的便签_返回null且不建布局()
    {
        var harness = CreateHarness();

        Assert.Null(harness.Service.OpenNote(Guid.NewGuid()));
        Assert.Empty(harness.Layouts.All);
    }

    [Fact]
    public void 打开全部_只返回上次关着之前是开着的便签()
    {
        var harness = CreateHarness();
        Note opened = NewNote("上次开着的");
        Note closed = NewNote("上次关着的");
        harness.Store.Add(opened);
        harness.Store.Add(closed);

        NoteLayout openLayout = harness.Layouts.GetOrCreate(opened.Id);
        openLayout.IsOpen = true;

        NoteLayout closedLayout = harness.Layouts.GetOrCreate(closed.Id);
        closedLayout.IsOpen = false;

        NoteOpenRequest request = Assert.Single(harness.Service.OpenAll());

        Assert.Same(opened, request.Note);
    }

    [Fact]
    public void 打开全部_不给没有布局记录的便签凭空建条目()
    {
        // §17.1 的裁决：首启不自动弹窗。一张从没被打开过的便签（没有布局记录）
        // 必须保持关闭。这里若顺手 GetOrCreate，它就会在下次启动时冒出来——
        // 用户第一次运行程序，屏幕上会突然堆满便签。
        var harness = CreateHarness();
        harness.Store.Add(NewNote("从没打开过"));

        Assert.Empty(harness.Service.OpenAll());
        Assert.Empty(harness.Layouts.All);
    }

    [Fact]
    public void 关闭便签_把IsOpen置为假并安排落盘()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);
        _ = harness.Service.OpenNote(note.Id);

        harness.Service.MarkNoteClosed(note.Id);

        Assert.False(harness.Layouts.TryGet(note.Id)!.IsOpen);
    }

    [Fact]
    public void 关闭没有布局记录的便签_什么也不做()
    {
        var harness = CreateHarness();
        Note note = NewNote("内容");
        harness.Store.Add(note);

        harness.Service.MarkNoteClosed(note.Id);

        Assert.Empty(harness.Layouts.All);
    }

    // ================= 外部变更（§10.3、§11.4） =================

    [Fact]
    public void 磁盘没变_什么也不做()
    {
        // §10.3 的自写抑制落在这一格上：我们刚保存完，监听器把那个事件报回来，
        // 读出来与上次同步的字节一模一样。它要是被当成外部改动，
        // 每一次自动保存都会触发一轮「重载」，把用户正在打的那行字顶掉。
        var harness = CreateHarness();
        Note local = NewNote("内容");
        harness.Store.Add(local);

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(DiskNote(local.FilePath, "另一个版本"), DiskChanged: false));

        Assert.Equal(ExternalChangeKind.None, result.Kind);
        Assert.Equal("内容", local.Content);
    }

    [Fact]
    public void 外面改了_本地没改_静默重载()
    {
        // §11.4 第一行：本地干净、磁盘变了，直接采用磁盘那一版。
        var harness = CreateHarness();
        Note local = NewNote("旧内容");
        harness.Store.Add(local);
        Note disk = DiskNote(local.FilePath, "# 新标题\n新内容", local.Id);

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Reloaded, result.Kind);
        Assert.Equal("# 新标题\n新内容", local.Content);

        // 引用的同一性在这里是硬要求：便签窗口绑的是这一个实例，换掉它，
        // 那扇窗就握着一张不在 Store 里的孤儿——用户在里面敲的字谁也收不到。
        Assert.Same(local, result.LocalNote);
        Assert.Same(local, harness.Store.TryGet(local.Id));

        // 索引也得跟着换，否则管理器搜到的还是旧内容。
        Assert.Contains("新标题", harness.Index.GetPlainText(local.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void 外面删了_便签从内存里摘干净()
    {
        var harness = CreateHarness();
        Note local = NewNote("内容");
        harness.Store.Add(local);

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(null, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Deleted, result.Kind);
        Assert.False(harness.Store.Contains(local.Id));
        Assert.Null(harness.Store.TryGetByPath(local.FilePath));
        Assert.Empty(harness.Index.GetPlainText(local.Id));
    }

    [Fact]
    public void 外面新建了一张_进内存并建好索引()
    {
        var harness = CreateHarness();
        Note disk = DiskNote(@"D:\notes\外面新建的.md", "外部写的内容");

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            disk.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Created, result.Kind);
        Assert.Same(disk, harness.Store.TryGet(disk.Id));
        Assert.Contains("外部写的内容", harness.Index.GetPlainText(disk.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void 同一张便签换了个路径_认成移动而不是删掉再新建()
    {
        // 外部把文件重命名或搬走了。身份来自 Front Matter 里的 id（§5.5），所以还是同一张便签。
        // 认成「删掉 + 新增」的话，那扇开着的窗会变成孤儿：它绑的实例已经不在 Store 里，
        // 用户在窗口里敲的字既进不了 Store 也存不下去（SaveNoteAsync 按 id 取到的是另一个对象）。
        var harness = CreateHarness();
        Note local = NewNote("内容");
        harness.Store.Add(local);
        string oldPath = local.FilePath;
        Note disk = DiskNote(@"D:\notes\换了名字.md", "内容", local.Id);

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            disk.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Reloaded, result.Kind);
        Assert.Same(local, harness.Store.TryGet(local.Id));
        Assert.Same(local, result.LocalNote);
        Assert.Equal(disk.FilePath, local.FilePath);

        // 老路径上不能再指着这张便签。留着的话，那个路径上再来一个事件会误伤它——
        // 「按路径找便签」是这条路上唯一的定位手段。
        Assert.Null(harness.Store.TryGetByPath(oldPath));
    }

    [Fact]
    public void 同一个路径换了身份_旧的那张进Replaced()
    {
        // 外部编辑器改写了 Front Matter 里的 id，或者文件被整个换掉（git 切分支最常见）。
        // 文件是权威，所以旧的摘掉、新的放进来——被顶掉的那一张必须交出去：
        // 它的 id 与磁盘上那张不同，调用方不显式关掉那扇窗的话，
        // 窗口会一直挂着一张内存里已经不存在的便签。
        var harness = CreateHarness();
        Note local = NewNote("旧身份的内容");
        harness.Store.Add(local);
        Note disk = DiskNote(local.FilePath, "新身份的内容");

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Created, result.Kind);
        Assert.Same(disk, result.LocalNote);
        Assert.Same(local, result.Replaced);
        Assert.False(harness.Store.Contains(local.Id));
        Assert.Same(disk, harness.Store.TryGet(disk.Id));
    }

    [Fact]
    public void 两边都改了_判成冲突交出去()
    {
        // §11.4 的真冲突：本地有没落盘的改动，磁盘上又是另一版。
        // 本层不替用户做主，只把两边都交出去，让 App 层去问（Core 不认识对话框）。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        Note disk = DiskNote(local.FilePath, "磁盘改过", local.Id);

        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Conflict, result.Kind);
        Assert.Same(local, result.LocalNote);
        Assert.Same(disk, result.Disk.Note);

        // 用户还没裁决，内存里一动不动。
        Assert.Equal("本地改过", local.Content);
    }

    [Fact]
    public async Task 存下去之后_再来的外部改动就不算冲突了()
    {
        // 「有没有未落盘的改动」是判冲突的唯一依据，而它必须被保存清掉。
        // 清不掉的话，用户编辑过一次之后无论存多少回，这张便签从此永远弹冲突框。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        await harness.Service.SaveNoteAsync(local.Id);

        Note disk = DiskNote(local.FilePath, "磁盘改过", local.Id);
        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Reloaded, result.Kind);
        Assert.Equal("磁盘改过", local.Content);
    }

    [Fact]
    public void 本地改过又与磁盘一样_静默接受而不是弹框()
    {
        // 判定顺序不能换：「内容本来就一样」要在「本地有没有未落盘的改动」之前。
        // 反过来的话，用户把改过的字又删回原样、而磁盘上恰好也是这一版时，
        // 他会收到一个无从回答的冲突框——两边一模一样，选哪个都没区别。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "改过的");
        harness.Service.ApplyLocalEdit(local, "原文");

        Note disk = DiskNote(local.FilePath, "原文", local.Id);
        ExternalChangeResult result = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Reloaded, result.Kind);
    }

    [Fact]
    public void 冲突选重新加载_本地实例被磁盘版覆盖而引用不变()
    {
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        Note disk = DiskNote(local.FilePath, "磁盘改过", local.Id);

        ExternalChangeResult conflict = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        harness.Service.ResolveConflictByReload(conflict);

        Assert.Same(local, harness.Store.TryGet(local.Id));
        Assert.Equal("磁盘改过", local.Content);
        Assert.Contains("磁盘改过", harness.Index.GetPlainText(local.Id), StringComparison.Ordinal);

        // 裁决过了，这张便签重新变干净：同一个文件再来一次改动不该又弹一次框。
        ExternalChangeResult again = harness.Service.ApplyExternalChange(
            local.FilePath,
            new NoteFileSync(DiskNote(local.FilePath, "磁盘又改了", local.Id), DiskChanged: true));

        Assert.Equal(ExternalChangeKind.Reloaded, again.Kind);
    }

    [Fact]
    public async Task 冲突选覆盖_先留副本再写回去()
    {
        // §11.4：用户点「覆盖外部版本」时心里想的是「我这份才是对的」，
        // 但万一他想错了，那份副本是他唯一的退路——所以备份必须发生在覆盖之前。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        Note disk = DiskNote(local.FilePath, "磁盘改过", local.Id);

        ExternalChangeResult conflict = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        await harness.Service.ResolveConflictByOverwriteAsync(conflict, Ct);

        Assert.Equal([local.FilePath], harness.Repository.BackedUpPaths);
        Assert.Same(local, Assert.Single(harness.Repository.Saved));
        Assert.Equal("本地改过", local.Content);
    }

    [Fact]
    public async Task 副本写不出来时_绝不覆盖磁盘()
    {
        // 副本没留成还照写不误的话，磁盘上那一版就真没了。用户点错那一档时，
        // 他丢掉的是另一个程序里的改动，而本程序连一句「备份失败」都没说。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        Note disk = DiskNote(local.FilePath, "磁盘改过", local.Id);

        ExternalChangeResult conflict = harness.Service.ApplyExternalChange(
            local.FilePath, new NoteFileSync(disk, DiskChanged: true));

        harness.Repository.BackupException = new IOException("磁盘满了");

        await Assert.ThrowsAsync<IOException>(
            () => harness.Service.ResolveConflictByOverwriteAsync(conflict, Ct));

        Assert.Empty(harness.Repository.Saved);
    }

    // ================= 整目录重扫（§10.4、§17.1 第 10 步） =================

    [Fact]
    public async Task 重扫时_把磁盘版搬进内存里那个实例()
    {
        // 托盘的「重新加载全部便签」与缓冲区溢出恢复走的都是这一条。
        // 早先的实现是「清空 Store 再逐条 Add」，那会换掉 Note 实例——
        // 而便签窗口绑的正是那个实例，重扫之后用户在窗口里敲的字谁也收不到。
        var harness = CreateHarness();
        Note local = NewNote("旧内容");
        harness.Store.Add(local);

        // 仓储交出来的是另一张对象：真实实现每读一次盘都会新建一个。
        harness.Repository.NotesToLoad.Add(DiskNote(local.FilePath, "磁盘上的新内容", local.Id));

        IReadOnlyList<ExternalChangeResult> changes = await harness.Service.LoadAllAsync(Ct);

        Assert.Same(local, harness.Store.TryGet(local.Id));
        Assert.Equal("磁盘上的新内容", local.Content);
        Assert.Equal(ExternalChangeKind.Reloaded, Assert.Single(changes).Kind);
    }

    [Fact]
    public async Task 重扫时_磁盘上没有的便签从内存里摘掉()
    {
        var harness = CreateHarness();
        Note local = NewNote("文件已经被删掉了");
        harness.Store.Add(local);

        IReadOnlyList<ExternalChangeResult> changes = await harness.Service.LoadAllAsync(Ct);

        Assert.False(harness.Store.Contains(local.Id));
        Assert.Equal(ExternalChangeKind.Deleted, Assert.Single(changes).Kind);
    }

    [Fact]
    public async Task 重扫时_没变的东西不算变化()
    {
        // 结果列表里只有真正变过的东西。全都报一遍的话，管理器会白白刷一轮列表，
        // 而选中项与滚动位置就在那一次刷新里丢掉。
        var harness = CreateHarness();
        Note local = NewNote("内容");
        harness.Store.Add(local);
        harness.Repository.NotesToLoad.Add(DiskNote(local.FilePath, "内容", local.Id));

        IReadOnlyList<ExternalChangeResult> changes = await harness.Service.LoadAllAsync(Ct);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task 重扫时_本地有未落盘的改动_同样判成冲突()
    {
        // 重扫与单文件外部变更共用同一段判定（Merge），于是「用户点托盘上的重新加载」
        // 与「外面有人改了文件」对待未落盘改动的方式必然一致，
        // 不会出现一条路静默覆盖、另一条路弹对话框这种半对半错的状态。
        var harness = CreateHarness();
        Note local = NewNote("原文");
        harness.Store.Add(local);
        harness.Service.ApplyLocalEdit(local, "本地改过");
        harness.Repository.NotesToLoad.Add(DiskNote(local.FilePath, "磁盘改过", local.Id));

        IReadOnlyList<ExternalChangeResult> changes = await harness.Service.LoadAllAsync(Ct);

        Assert.Equal(ExternalChangeKind.Conflict, Assert.Single(changes).Kind);
    }

    // ---- 新建（§3.3 流 3） ----

    [Fact]
    public async Task 新建_把仓储建出来的便签放进内存与索引()
    {
        var harness = CreateHarness();

        // 文件名分配与首次写盘是仓储的活（§5.6），这里要验的只是编排：
        // 它有没有把仓储交出来的那一张真的收进内存、并通知索引。
        Note created = NewNote("# 购物清单\n- 牛奶");
        created.Tags = ["家务"];
        harness.Repository.CreateHandler = () => created;

        Note returned = await harness.Service.CreateNoteAsync();

        Assert.Same(created, returned);

        // 进了内存，管理器列表里才看得见它。
        Assert.Same(created, harness.Store.TryGet(created.Id));

        // 通知了索引，它才搜得到、也才出现在标签侧栏里。
        // 索引里存的是去掉标记之后的纯文本（§9.4），所以这里比的是关键词而不是原文；
        // 没被索引过的 id 拿出来是空串，因此这一条足以证明 OnNoteAdded 真的跑过。
        Assert.Contains("牛奶", harness.Index.GetPlainText(created.Id), StringComparison.Ordinal);
        Assert.Contains(created.Id, harness.Index.NotesWithTag("家务")!);
    }

    [Fact]
    public async Task 新建_把颜色与目标文件夹原样交给仓储()
    {
        var harness = CreateHarness();

        await harness.Service.CreateNoteAsync(NoteColor.Blue, "工作");

        var request = Assert.Single(harness.Repository.CreateRequests);
        Assert.Equal(NoteColor.Blue, request.Color);
        Assert.Equal("工作", request.TargetFolder);
    }

    [Fact]
    public async Task 新建_不自己去建布局条目()
    {
        var harness = CreateHarness();

        Note created = await harness.Service.CreateNoteAsync();

        // 布局是设备状态，谁开窗谁负责（§8.3）。在这里顺手建一条，
        // 「新建了但没开窗」的情形就会在 layout.json 里留下一条永远用不上的记录。
        Assert.Empty(harness.Layouts.All);
        Assert.False(harness.Timers.Last.IsRunning);
        Assert.Null(harness.Layouts.TryGet(created.Id));
    }

    [Fact]
    public async Task 新建_仓储抛异常时原样往上传()
    {
        var harness = CreateHarness();
        harness.Repository.CreateException = new InvalidOperationException("笔记目录不存在。");

        // 变成「静默返回 null」的话，界面上就是「点了新建什么都没有」——
        // 用户只能一遍遍地点，而不知道为什么。异常要传上去让上层去说清楚。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.CreateNoteAsync());

        Assert.Equal(0, harness.Store.Count);
    }

    // ---- 辅助 ----

    private static Harness CreateHarness()
    {
        var store = new NoteStore();
        var index = new SearchIndex();
        var repository = new FakeNoteRepository();
        var layouts = new InMemoryLayoutStore();
        var timers = new ManualUiTimerFactory();
        var clock = new FakeClock();
        var trashStore = new FakeTrashStore();

        var layoutService = new LayoutService(layouts, FixedDisplayProvider.Single(), timers);

        // NoteService 的删除与恢复只是转发给 TrashService，所以这里要一整条真实的
        // TrashService——用替身包替身的话，「转发到底通没通」就测不出来了。
        var trashService = new TrashService(trashStore, new FakeAppPaths(), store, index, repository);

        var service = new NoteService(store, index, repository, layoutService, trashService, clock);

        return new Harness(service, store, index, repository, layouts, timers, clock, trashStore, trashService);
    }

    private static Note NewNote(string content, Guid? id = null)
    {
        Guid noteId = id ?? Guid.NewGuid();

        return new Note
        {
            Id = noteId,
            FilePath = $@"D:\notes\{noteId:N}.md",
            Content = content,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }

    /// <summary>
    /// 磁盘上那一份：内容与 id 随用例定，<strong>路径必须显式给</strong>——
    /// 「按路径找到内存里那张便签」是外部变更那条路上唯一的定位手段。
    /// </summary>
    private static Note DiskNote(string path, string content, Guid? id = null)
    {
        Note note = NewNote(content, id);
        note.FilePath = path;

        return note;
    }

    private sealed record Harness(
        NoteService Service,
        NoteStore Store,
        SearchIndex Index,
        FakeNoteRepository Repository,
        InMemoryLayoutStore Layouts,
        ManualUiTimerFactory Timers,
        FakeClock Clock,
        FakeTrashStore Trash,
        TrashService TrashService);
}
