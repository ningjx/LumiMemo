using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Messages;
using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using Xunit;

namespace LumiMemo.App.Tests.ViewModels;

/// <summary>
/// <see cref="ManagerViewModel"/> 的单元测试：列表、搜索接线与去抖（§12.1、§15.8）。
/// </summary>
/// <remarks>
/// <para>
/// 本类<strong>不测</strong>评分与摘要本身——那是 <c>LumiMemo.Core.Tests</c> 的
/// <c>NoteSearchTests</c> 与 <c>SnippetBuilderTests</c> 的事。这里只关心接线：
/// 查询词为空与不为空时管理器<em>分别走了哪条路</em>、去抖有没有真的把搜索推后、
/// 结果超过上限怎么办。
/// </para>
/// <para>
/// 便签直接塞进 <see cref="NoteStore"/>，不走 <c>INoteService.LoadAllAsync</c>：
/// 扫描目录是 Integration.Tests 的事，在这里造一份真目录只会让用例变慢且更容易碎。
/// </para>
/// </remarks>
public sealed class ManagerViewModelTests
{
    // ================= 空查询：直接取快照 =================

    [Fact]
    public void 没有查询词时列出全部便签()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 一", updatedAt: ManagerHarness.AtHours(1)));
        h.Add(ManagerHarness.NewNote("# 二", updatedAt: ManagerHarness.AtHours(2)));
        h.Add(ManagerHarness.NewNote("# 三", updatedAt: ManagerHarness.AtHours(3)));

        h.Vm.Refresh();

        // 这一条同时钉住了「空查询不走 Search」：§12.1 规定空查询返回空列表，
        // 若哪天真把两条路径合并了，这里会变成 0 条而不是 3 条。
        Assert.Equal(3, h.Vm.Notes.Count);
    }

    [Fact]
    public void 没有查询词时按修改时间倒序()
    {
        using var h = new ManagerHarness();
        var oldest = ManagerHarness.NewNote("# 一", updatedAt: ManagerHarness.AtHours(1));
        var newest = ManagerHarness.NewNote("# 二", updatedAt: ManagerHarness.AtHours(9));
        var middle = ManagerHarness.NewNote("# 三", updatedAt: ManagerHarness.AtHours(5));

        h.Add(oldest);
        h.Add(newest);
        h.Add(middle);

        h.Vm.Refresh();

        Assert.Equal(
            new[] { newest.Id, middle.Id, oldest.Id },
            h.Vm.Notes.Select(static item => item.Id).ToArray());
    }

    [Fact]
    public void 没有查询词时副标题是修改时间与字数()
    {
        using var h = new ManagerHarness();
        var content = "# 标题\n" + new string('内', 48);

        h.Add(ManagerHarness.NewNote(content, updatedAt: ManagerHarness.AtHours(1)));
        h.Vm.Refresh();

        // 时刻那一截断言形状而不是具体值：LocalDateTime 会把便签带的偏移换成本机时区，
        // 钉死"1/1 08:00"会让这条用例只在 UTC+8 的机器上通过。
        // 字数那一截钉的是 Note.Content.Length——<strong>含 Markdown 标记</strong>，
        // 这是有意的（用户看到的"重"与文件里的字节数一致，不会因为 `#` 而少一个）。
        var subtitle = Assert.Single(h.Vm.Notes).Subtitle;

        Assert.Matches($@"^\d{{1,2}}/\d{{1,2}} \d{{2}}:\d{{2}} · {content.Length} 字$", subtitle);
    }

    // ================= 有查询词：走 Search =================

    [Fact]
    public void 有查询词时只列出命中的便签()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));
        h.Add(ManagerHarness.NewNote("# 别的\n与此无关"));

        h.SettleQuery("文档");

        Assert.Single(h.Vm.Notes);
        Assert.Equal("笔记", Assert.Single(h.Vm.Notes).Title);
    }

    [Fact]
    public void 查询词没有命中时列表为空且提示换了文案()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记"));

        h.SettleQuery("找不到的词");

        Assert.Empty(h.Vm.Notes);
        Assert.Equal("没有找到匹配的便签。", h.Vm.EmptyHint);
        Assert.Equal("找到 0 条", h.Vm.CountText);
    }

    [Fact]
    public void 副标题在正文命中时切成摘要并标出命中段()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n今天读了文档，记一下心得"));

        h.SettleQuery("文档");

        var segments = Assert.Single(h.Vm.Notes).SubtitleSegments;

        Assert.Equal("文档", Assert.Single(segments, static s => s.IsMatch).Text);

        // 摘要从纯文本摘，而标题那一行本来就在纯文本里（SearchIndex 拿的是整篇正文），
        // 于是它会一起出现在摘要里。看着有点重复，但这是 §12.3 定的做法——
        // 改掉这一条的前提是先改 §12.3，不是在这里加一个"跳过开头"的特例。
        Assert.Equal("笔记 今天读了文档，记一下心得", string.Concat(segments.Select(static s => s.Text)));
    }

    [Fact]
    public void 只命中标签时副标题退回修改时间()
    {
        // 标签命中摘不出正文片段，而摘一段"不包含查询词"的正文出来毫无意义——
        // 用户看不出这一段为什么在这儿。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记", updatedAt: ManagerHarness.AtHours(1));
        note.Tags.Add("文档");

        h.Add(note);
        h.SettleQuery("文档");

        var item = Assert.Single(h.Vm.Notes);

        Assert.False(Assert.Single(item.SubtitleSegments).IsMatch);
        Assert.Matches(@"^\d{1,2}/\d{1,2} \d{2}:\d{2} · \d+ 字$", item.Subtitle);
    }

    [Fact]
    public void 命中标题时副标题照样给摘要()
    {
        // 标题那一行<strong>本身就在纯文本里</strong>（SearchIndex 拿的是整篇正文），
        // 所以"命中标题"必然同时是一次正文命中，摘得出片段。
        // 这条用例是为了把这个反直觉的结论钉住：将来若有人改了 ToPlainText 不再包含标题行，
        // 这里会红，而不是让用户先发现"搜标题的便签副标题变成日期了"。
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 文档"));

        h.SettleQuery("文档");

        Assert.Equal("文档", Assert.Single(Assert.Single(h.Vm.Notes).SubtitleSegments, static s => s.IsMatch).Text);
    }

    [Fact]
    public void 置顶的便签排在搜索结果前面()
    {
        using var h = new ManagerHarness();
        var pinned = ManagerHarness.NewNote("# 笔记甲\n文档在这里放着");
        var plain = ManagerHarness.NewNote("# 笔记乙\n文档在这里放着");

        h.Add(pinned);
        h.Add(plain);
        h.Pin(pinned.Id);

        h.SettleQuery("文档");

        Assert.Equal(pinned.Id, h.Vm.Notes[0].Id);
    }

    [Fact]
    public void 清空查询词后回到按修改时间倒序()
    {
        using var h = new ManagerHarness();

        // 有意让两条路径给出<strong>相反</strong>的顺序：标题完全命中的那条分最高
        // （1000 那一档压过正文的 200），但它更旧，倒序时该排后面。
        // 这样这条用例才真的在区分两条路径，而不是恰好撞上同一个结果。
        var olderButRelevant = ManagerHarness.NewNote("# 文档", updatedAt: ManagerHarness.AtHours(1));
        var newerButWeak = ManagerHarness.NewNote("# 别的东西\n文档在这里放着", updatedAt: ManagerHarness.AtHours(9));

        h.Add(olderButRelevant);
        h.Add(newerButWeak);

        h.SettleQuery("文档");
        Assert.Equal(olderButRelevant.Id, h.Vm.Notes[0].Id);

        h.SettleQuery(string.Empty);

        Assert.Equal(2, h.Vm.Notes.Count);
        Assert.Equal(newerButWeak.Id, h.Vm.Notes[0].Id);
        Assert.Equal(olderButRelevant.Id, h.Vm.Notes[1].Id);
    }

    // ================= 去抖 =================

    [Fact]
    public void 输入查询词只是起定时器_还没到期时不搜()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));

        h.Vm.SearchQuery = "文档";

        // 列表还是空的：构造之后没有 Refresh 过，而定时器还没到期。
        Assert.Empty(h.Vm.Notes);
        Assert.True(h.SearchTimer.IsRunning);
    }

    [Fact]
    public void 连打几个字只按最后一次搜()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));

        h.Vm.SearchQuery = "文";
        h.Vm.SearchQuery = "文档";
        h.Vm.SearchQuery = "文档库";

        // 每次都重新计时而不是排队（IUiTimer.Start 的语义：从本次调用算起）。
        // 注意这里不经过 Stop——Start 自己就覆盖了上一轮，Stop 是被 Free 与手动刷新用的。
        Assert.Equal(3, h.SearchTimer.StartCount);
        Assert.Equal(0, h.SearchTimer.StopCount);

        h.SearchTimer.Fire();

        Assert.Empty(h.Vm.Notes);
    }

    [Fact]
    public void 定时器到期后才真的搜()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));

        h.Vm.SearchQuery = "文档";
        h.SearchTimer.Fire();

        Assert.Single(h.Vm.Notes);
    }

    [Fact]
    public void 去抖时长取自可写属性且默认一百五十毫秒()
    {
        using var h = new ManagerHarness();

        h.Vm.SearchQuery = "文档";

        Assert.Equal(150, h.Vm.SearchDebounceMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(150), h.SearchTimer.Interval);

        h.Vm.SearchDebounceMilliseconds = 400;
        h.Vm.SearchQuery = "文档库";

        Assert.Equal(TimeSpan.FromMilliseconds(400), h.SearchTimer.Interval);
    }

    [Fact]
    public void 手动刷新会撤掉还没到期的去抖()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));

        h.Vm.SearchQuery = "文档";
        h.Vm.Refresh();

        // 到这一步列表已经是正确的结果了；那条还没到期的定时器再跑一次只会得出同一份列表。
        Assert.False(h.SearchTimer.IsRunning);
        Assert.Single(h.Vm.Notes);
    }

    // ================= 渲染上限 =================

    [Fact]
    public void 结果超过两百条时只渲染两百条并提示还有多少()
    {
        using var h = new ManagerHarness();

        for (var i = 0; i < ManagerViewModel.MaxRenderedResults + 50; i++)
        {
            h.Add(ManagerHarness.NewNote($"# 笔记 {i}\n文档在这里放着"));
        }

        h.SettleQuery("文档");

        Assert.Equal(ManagerViewModel.MaxRenderedResults, h.Vm.Notes.Count);
        Assert.True(h.Vm.HasOverflow);
        Assert.Equal("还有 50 条结果，请细化搜索词", h.Vm.OverflowHint);

        // 计数说的是命中总数，不是渲染出来的条数。
        Assert.Equal("找到 250 条", h.Vm.CountText);
    }

    [Fact]
    public void 结果没超过两百条时不提示()
    {
        using var h = new ManagerHarness();
        h.Add(ManagerHarness.NewNote("# 笔记\n文档在这里放着"));

        h.SettleQuery("文档");

        Assert.False(h.Vm.HasOverflow);
        Assert.Equal(string.Empty, h.Vm.OverflowHint);
    }

    // ================= 计数与提示 =================

    [Fact]
    public void 计数与空列表提示按有无查询词切换()
    {
        using var h = new ManagerHarness();

        h.Vm.Refresh();
        Assert.Equal("0 条便签", h.Vm.CountText);
        Assert.Equal("这个文件夹里还没有便签。", h.Vm.EmptyHint);

        h.Add(ManagerHarness.NewNote("# 笔记\n提一下文档"));
        h.SettleQuery("文档");

        Assert.Equal("找到 1 条", h.Vm.CountText);
    }

    // ================= 开窗 =================

    [Fact]
    public void 双击一行会按该行开出一张便签()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.NoteService.OpenNoteHandler = id =>
            id == note.Id ? new NoteOpenRequest(note, new NoteLayout { NoteId = note.Id }) : null;

        h.Vm.OpenNote(h.Vm.Notes[0]);

        Assert.Equal($"ShowNote({note.Id})", Assert.Single(h.Windows.Calls));
    }

    [Fact]
    public void 打开一条列表里已经没有的便签时什么都不做()
    {
        // 列表是上一轮的快照，文件被外部删掉之后点开它就会出现这种情况。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.NoteService.OpenNoteHandler = static _ => null;

        h.Vm.OpenNote(h.Vm.Notes[0]);

        Assert.Empty(h.Windows.Calls);
    }

    // ================= 「显示全部便签」的零窗口兜底 =================

    [Fact]
    public void 一张便签都没打开时显示全部会退回到管理器窗口()
    {
        // §17.6 那三步只覆盖了「有便签可显示」的情形。而这条路有两个入口用户换不掉
        // ——双击托盘图标与再启动一个实例——所以一张窗口都亮不出来时必须给点反应，
        // 否则程序没有任何界面时点桌面图标毫无动静，与「坏了」没有区别。
        using var h = new ManagerHarness();

        Assert.Empty(h.NoteService.OpenAllResult);

        h.Vm.ShowAll();

        Assert.Equal(1, h.Presenter.BringToFrontCount);

        // 窗口层那一步照走：兜底是加在原有的两步之后，不是替换掉它们。
        Assert.Contains("ShowAllNotes()", h.Windows.Calls);
    }

    [Fact]
    public void 有便签打开时显示全部不碰管理器窗口()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.NoteService.OpenAllResult.Add(new NoteOpenRequest(note, new NoteLayout { NoteId = note.Id }));

        h.Vm.ShowAll();

        Assert.Equal(0, h.Presenter.BringToFrontCount);
        Assert.Contains($"ShowNote({note.Id})", h.Windows.Calls);
    }

    // ================= 启动恢复上次开着的便签（§17.1 第 11 步） =================

    [Fact]
    public async Task 启动恢复_分批开窗_让出控制权时已经开出来的张数按批次走()
    {
        // 这条用例验的不是「最后开了几张」——一个把七扇窗一口气开完的实现也开七张。
        // 验的是批次之间真的松了手，以及松手时已经开到第几张：
        // 第一帧三个，之后每帧两个（§17.1 第 11 步的要点）。
        using var h = new ManagerHarness();

        List<Note> notes = [.. Enumerable.Range(0, 7).Select(_ => ManagerHarness.NewNote("# 便签"))];

        foreach (Note note in notes)
        {
            h.Add(note);
            h.NoteService.OpenAllResult.Add(new NoteOpenRequest(note, new NoteLayout { NoteId = note.Id }));
        }

        List<int> openedWhenYielded = [];

        h.Dispatcher.OnYield = () => openedWhenYielded.Add(ShownCount(h));

        int restored = await h.Vm.RestoreOpenNotesAsync();

        // 让帧两次：3 → 5。最后一批开完不再让（那时已经无事可做），
        // 所以七张对应 [3, 5] 而不是 [3, 5, 7]。
        Assert.Equal(new[] { 3, 5 }, openedWhenYielded);
        Assert.Equal(7, restored);

        // 开出来的正是 OpenAll() 给的那几张、顺序一致：恢复窗口不自己挑拣也不重排。
        Assert.Equal(
            notes.Select(note => $"ShowNote({note.Id})"),
            h.Windows.Calls.Where(call => call.StartsWith("ShowNote(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 启动恢复_三张以内一次开完_批次之间不让帧()
    {
        // 边界：恰好一个批次。让帧的意义是「给界面喘口气」，没有下一批要跑的时候让它
        // 只会让调用方白等一轮消息泵——而首启时多数用户就只有那么两三张便签。
        using var h = new ManagerHarness();

        for (int i = 0; i < 3; i++)
        {
            Note note = ManagerHarness.NewNote("# 便签");

            h.Add(note);
            h.NoteService.OpenAllResult.Add(new NoteOpenRequest(note, new NoteLayout { NoteId = note.Id }));
        }

        Assert.Equal(3, await h.Vm.RestoreOpenNotesAsync());
        Assert.Empty(h.Dispatcher.YieldOrder);
    }

    [Fact]
    public async Task 启动恢复_没有该恢复的便签时什么都不做()
    {
        // 首启就是这个样子：没有 layout.json，OpenAll() 返回空。
        // 「不判断是不是第一次启动」这件事全靠这条——判据只有一个，
        // 于是不可能出现「首启弹了」或「重开不恢复」这种一半对一半错的状态。
        using var h = new ManagerHarness();

        Assert.Empty(h.NoteService.OpenAllResult);

        Assert.Equal(0, await h.Vm.RestoreOpenNotesAsync());
        Assert.Empty(h.Windows.Calls);
        Assert.Empty(h.Dispatcher.YieldOrder);

        // 与 ShowAll 刻意不同：一张都没恢复时**不**去把管理器窗口带到前台。
        // 那一步是给「用户主动要求显示全部便签」用的兜底，而启动时管理器
        // 已经在 StartupSequence 里显示并且激活过了（§17.3）。
        Assert.Equal(0, h.Presenter.BringToFrontCount);
    }

    // ================= 删除（列表项右键菜单） =================

    [Fact]
    public async Task 删除没开窗的便签会移入回收站并刷新列表()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        await h.Vm.DeleteNoteAsync(h.Vm.Notes[0]);

        Assert.Equal(new[] { note.Id }, h.NoteService.DeletedNoteIds);

        // 窗口没开着就不必补落盘：编辑只可能来自一个开着的窗口，
        // 而它在关掉的时候已经存过了。窗口层同样不该被打扰。
        Assert.Empty(h.NoteService.SavedNoteIds);
        Assert.Empty(h.Windows.Calls);

        // 计数与空列表提示都挂在 Refresh 上，所以删除必须走它而不是只摘一行。
        Assert.Empty(h.Vm.Notes);
        Assert.Equal("0 条便签", h.Vm.CountText);
    }

    [Fact]
    public async Task 删除开着的便签会赶在搬文件之前补一次落盘()
    {
        // 便签窗口关闭时自己会存一次（§17.3），但那一次排在消息队列里，
        // 而 CloseNote 返回时它还没跑。若这时就把文件搬进回收站，等它跑起来
        // 便签已经不在 NoteStore 里，SaveNoteAsync 会静默返回（那是它刻意为之的行为）
        // ——用户最后半秒敲的字既没进文件也没进回收站。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.NoteService.OpenNoteHandler = id =>
            id == note.Id ? new NoteOpenRequest(note, new NoteLayout { NoteId = note.Id }) : null;
        h.Vm.OpenNote(h.Vm.Notes[0]);

        await h.Vm.DeleteNoteAsync(h.Vm.Notes[0]);

        // 搬走的那一刻，这张便签已经落过盘了。
        Assert.Equal(new[] { true }, h.NoteService.SavedBeforeDelete);

        // 窗口也要关掉。留着的话用户会对着一张已经删掉的便签继续打字，
        // 而那些字哪儿都去不了——他会以为自己删失败了。
        Assert.Contains($"CloseNote({note.Id})", h.Windows.Calls);
        Assert.Equal(new[] { note.Id }, h.NoteService.DeletedNoteIds);
    }

    [Fact]
    public async Task 删除一条列表里已经没有的便签时什么都不做()
    {
        // 列表是上一轮的快照，文件被别处删掉之后右键它就会走到这里。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        var stale = h.Vm.Notes[0];

        h.Store.Remove(note.Id);

        await h.Vm.DeleteNoteAsync(stale);

        Assert.Empty(h.NoteService.DeletedNoteIds);
    }

    // ================= 过滤条（§15.8） =================

    [Fact]
    public void 只看置顶时筛掉没置顶的()
    {
        using var h = new ManagerHarness();
        var pinned = ManagerHarness.NewNote("# 置顶的");
        var plain = ManagerHarness.NewNote("# 没置顶的");

        h.Add(pinned);
        h.Add(plain);
        h.Pin(pinned.Id);

        h.Vm.FilterTopMost = true;

        Assert.Equal(pinned.Id, Assert.Single(h.Vm.Notes).Id);

        // 计数与空提示都挂在 Refresh 上，过滤走的是它，所以三处文案一起换了。
        Assert.Equal("筛选出 1 条", h.Vm.CountText);
        Assert.False(h.Vm.IsFilterAll);
    }

    [Fact]
    public void 只看有标签时筛掉没标签的()
    {
        using var h = new ManagerHarness();
        var tagged = ManagerHarness.NewNote("# 有标签的");
        var bare = ManagerHarness.NewNote("# 没标签的");

        tagged.Tags.Add("工作");

        h.Add(tagged);
        h.Add(bare);

        h.Vm.FilterTagged = true;

        Assert.Equal(tagged.Id, Assert.Single(h.Vm.Notes).Id);
    }

    [Fact]
    public void 只看最近七天时筛掉更旧的()
    {
        using var h = new ManagerHarness();
        var fresh = ManagerHarness.NewNote("# 今天改的", updatedAt: ManagerHarness.AtHours(1));

        // 时钟拨在 2026-09-19 12:00Z，往回两百小时是 9 月 10 日，刚好在七天之外。
        var stale = ManagerHarness.NewNote("# 很久没动过", updatedAt: ManagerHarness.AtHours(-200));

        h.Add(fresh);
        h.Add(stale);

        h.Vm.FilterRecent = true;

        Assert.Equal(fresh.Id, Assert.Single(h.Vm.Notes).Id);
    }

    [Fact]
    public void 勾了多个条件时是且的关系()
    {
        using var h = new ManagerHarness();
        var both = ManagerHarness.NewNote("# 都满足");
        var onlyPinned = ManagerHarness.NewNote("# 只置顶");
        var onlyTagged = ManagerHarness.NewNote("# 只有标签");

        both.Tags.Add("工作");
        onlyTagged.Tags.Add("工作");

        h.Add(both);
        h.Add(onlyPinned);
        h.Add(onlyTagged);

        h.Pin(both.Id);
        h.Pin(onlyPinned.Id);

        h.Vm.FilterTopMost = true;
        h.Vm.FilterTagged = true;

        // 「与」而不是「或」：两个条件是叠加的约束，不是两个互不相干的入口。
        Assert.Equal(both.Id, Assert.Single(h.Vm.Notes).Id);
    }

    [Fact]
    public void 过滤在搜索之前_筛掉的不参与评分也不计数()
    {
        using var h = new ManagerHarness();
        var tagged = ManagerHarness.NewNote("# 甲\n文档在这里放着");
        var bare = ManagerHarness.NewNote("# 乙\n文档在这里放着");

        tagged.Tags.Add("工作");

        h.Add(tagged);
        h.Add(bare);

        h.Vm.FilterTagged = true;
        h.SettleQuery("文档");

        Assert.Equal(tagged.Id, Assert.Single(h.Vm.Notes).Id);

        // 计数读的是筛过之后的 _matches。若过滤排在搜索后面，
        // 这里会报「找到 2 条」而列表里只有一行，用户对不上账。
        Assert.Equal("找到 1 条", h.Vm.CountText);
    }

    [Fact]
    public void 过滤把自己筛空时提示说的是筛选而不是没有便签()
    {
        using var h = new ManagerHarness();

        h.Add(ManagerHarness.NewNote("# 没标签的"));

        h.Vm.FilterTagged = true;

        Assert.Empty(h.Vm.Notes);
        Assert.Equal("筛选出 0 条", h.Vm.CountText);

        // 分开一句：文件夹里明明有便签却说"还没有便签"，用户会以为程序没扫到文件，
        // 而真正的原因是他自己勾了一个过滤条件。
        Assert.Equal("没有符合筛选条件的便签。", h.Vm.EmptyHint);
    }

    [Fact]
    public void 清空过滤时列表只重建一遍()
    {
        using var h = new ManagerHarness();

        h.Add(ManagerHarness.NewNote("# 笔记"));

        h.Vm.FilterTopMost = true;
        h.Vm.FilterTagged = true;
        h.Vm.FilterRecent = true;

        // Refresh 每次都会撤掉还没到期的去抖搜索，所以这个计数就是"列表被重建了几遍"。
        int before = h.SearchTimer.StopCount;

        h.Vm.ClearFilters();

        // 三个条件是一个一个赋的，中间那两次过渡态不该各刷一遍——
        // 不加 _clearingFilters 的话这里是 +3，点一下「全部」肉眼可见地卡三下。
        Assert.Equal(before + 1, h.SearchTimer.StopCount);

        Assert.False(h.Vm.FilterTopMost);
        Assert.False(h.Vm.FilterTagged);
        Assert.False(h.Vm.FilterRecent);

        // 「全部」不是第四个条件，它是"三个都没勾"这个状态本身。
        Assert.True(h.Vm.IsFilterAll);
    }

    [Fact]
    public void 清空过滤会让三个拨动按钮收到通知弹回来()
    {
        // 这条钉的是实现手法：ClearFilters 里是逐个给属性赋值（走 PropertyChanged），
        // 不是图省事直接写后备字段。后者连通知都没有，用户会看着三个按钮全亮着、
        // 列表却是全部。工具包的 MVVMTK0034 也正是为了拦这一手。
        using var h = new ManagerHarness();

        h.Vm.FilterTopMost = true;
        h.Vm.FilterTagged = true;
        h.Vm.FilterRecent = true;

        var changed = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Vm.ClearFilters();

        Assert.Contains(nameof(ManagerViewModel.FilterTopMost), changed);
        Assert.Contains(nameof(ManagerViewModel.FilterTagged), changed);
        Assert.Contains(nameof(ManagerViewModel.FilterRecent), changed);

        // 「全部」那一档也得跟着亮起来，否则界面看上去还是"在筛"。
        Assert.Contains(nameof(ManagerViewModel.IsFilterAll), changed);
    }

    [Fact]
    public void 没有勾任何条件时清空过滤什么都不做()
    {
        using var h = new ManagerHarness();

        h.Vm.Refresh();

        int before = h.SearchTimer.StopCount;

        h.Vm.ClearFilters();

        Assert.Equal(before, h.SearchTimer.StopCount);
    }

    // ================= 右键菜单：置顶 =================

    [Fact]
    public void 右键置顶会写进布局并发消息告诉已开着的窗口()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        NoteTopMostChangedMessage? received = null;
        h.Messenger.Register<NoteTopMostChangedMessage>(this, (_, message) => received = message);

        h.Vm.ToggleTopMost(h.Vm.Notes[0]);

        Assert.True(h.LayoutStore.GetOrCreate(note.Id).IsTopMost);

        // 置顶落在 layout.json 上（§18.1 的三类状态里它归窗口状态那一类），
        // 所以标脏是必须的——否则这次点击重启之后就没了。
        Assert.True(h.LayoutStore.IsDirty);

        // 开着的便签窗口另存一份镜像，而布局层的写入不会发出任何通知。
        // 少了这条消息，窗口既不真的置顶、标题条上的按钮也还显示着旧状态。
        Assert.True(received is not null);
        Assert.Equal(note.Id, received.NoteId);
        Assert.True(received.IsTopMost);
    }

    [Fact]
    public void 再点一次置顶是取消()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        h.Vm.ToggleTopMost(h.Vm.Notes[0]);
        h.Vm.ToggleTopMost(h.Vm.Notes[0]);

        Assert.False(h.LayoutStore.GetOrCreate(note.Id).IsTopMost);
    }

    [Fact]
    public void 置顶一张列表里已经没有的便签时什么都不做()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        var stale = h.Vm.Notes[0];

        h.Store.Remove(note.Id);

        h.Vm.ToggleTopMost(stale);

        Assert.False(h.LayoutStore.TryGet(note.Id)?.IsTopMost ?? false);
    }

    [Fact]
    public void 右键菜单的标题跟着选中行的置顶状态换()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        var changed = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // 没有选中行时无从置顶，写的还是默认那一句。
        Assert.Equal("置顶", h.Vm.TopMostMenuHeader);

        h.Vm.SelectedNote = h.Vm.Notes[0];

        Assert.Contains(nameof(ManagerViewModel.TopMostMenuHeader), changed);

        // 便签窗口标题条上的置顶按钮改的也是布局层，管理器这一侧毫无察觉。
        h.Pin(note.Id);
        changed.Clear();

        // 读一把就是新值——它是现算的，不是存下来的。
        Assert.Equal("取消置顶", h.Vm.TopMostMenuHeader);

        // 但"选中没变所以一声通知都不发"这件事仍然成立，
        // 而菜单是挂在整个 ListBox 上的同一个实例，重开时也不会自己去重读。
        Assert.Empty(changed);

        // 于是 ManagerWindow 在菜单弹出前显式喊这一声。
        h.Vm.NotifyContextMenuOpening();

        Assert.Contains(nameof(ManagerViewModel.TopMostMenuHeader), changed);
    }

    // ================= 右键菜单：在资源管理器中显示 =================

    [Fact]
    public async Task 在资源管理器中显示会把文件路径交给外壳()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        await h.Vm.RevealInExplorerAsync(h.Vm.Notes[0]);

        // 交出去的是文件而不是目录：要的是"选中它"，不是"打开所在目录"。
        Assert.Equal(new[] { note.FilePath }, h.Shell.RevealedFiles);
        Assert.Empty(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public async Task 文件已经不在了时提示用户而不是静默返回()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();

        h.Shell.Result = false;

        await h.Vm.RevealInExplorerAsync(h.Vm.Notes[0]);

        // 用户刚点了一下按钮，什么都不发生的话他只会以为程序卡住了。
        Assert.Equal(
            $"管理器|找不到这个文件：\n{note.FilePath}",
            Assert.Single(h.Dialogs.ErrorRequests));
    }

    [Fact]
    public async Task 没有选中行时在资源管理器中显示什么都不做()
    {
        using var h = new ManagerHarness();

        await h.Vm.RevealInExplorerAsync(null);

        Assert.Empty(h.Shell.RevealedFiles);
        Assert.Empty(h.Dialogs.ErrorRequests);
    }

    // ================= 右键菜单：颜色 =================

    [Fact]
    public async Task 改颜色会写回便签并立刻落盘()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        await h.Vm.SetColorAsync(NoteColor.Blue);

        Assert.Equal(NoteColor.Blue, note.Color);
        Assert.Equal([(note.Id, NoteColor.Blue)], h.NoteService.ColorEdits);

        // 一次点完就结束的动作，等去抖没有意义：用户改完颜色随即关掉程序，
        // 那几百毫秒就成了纯粹的丢数据窗口。
        Assert.Equal(new[] { note.Id }, h.NoteService.SavedNoteIds);
    }

    [Fact]
    public async Task 改颜色之后列表会重建()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        var before = h.Vm.Notes[0];

        await h.Vm.SetColorAsync(NoteColor.Green);

        // 刷新与否，卡片上那个颜色点都是新值（它是直接读 Note 的），
        // 所以这里只能靠实例不同来验列表确实重建了一遍。重建的意义在别处：
        // UpdatedAt 已经动了，无查询词时那一列按它排序，摘要与计数也要重算。
        Assert.NotSame(before, h.Vm.Notes[0]);
    }

    [Fact]
    public async Task 点了当前那一个颜色时什么都不做()
    {
        // 白白走一趟的话 ApplyColorEdit 会刷新 UpdatedAt，于是列表按修改时间重排——
        // 用户只是点了一下「确认还是这个颜色」，却看到这一行跳到别处去了。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Color = NoteColor.Blue;

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        await h.Vm.SetColorAsync(NoteColor.Blue);

        Assert.Empty(h.NoteService.ColorEdits);
        Assert.Empty(h.NoteService.SavedNoteIds);
        Assert.Equal(ManagerHarness.AtHours(0), note.UpdatedAt);
    }

    [Fact]
    public async Task 没有选中行时改颜色什么都不做()
    {
        using var h = new ManagerHarness();

        await h.Vm.SetColorAsync(NoteColor.Pink);

        Assert.Empty(h.NoteService.ColorEdits);
    }

    [Fact]
    public async Task 改一张列表里已经没有的便签的颜色时什么都不做()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Store.Remove(note.Id);

        await h.Vm.SetColorAsync(NoteColor.Purple);

        Assert.Empty(h.NoteService.ColorEdits);
        Assert.Empty(h.NoteService.SavedNoteIds);
    }

    [Fact]
    public async Task 改颜色落盘失败时提示用户但不回滚内存()
    {
        // 与 §11.5 的策略一致：用户改的东西还在，下一次改动或退出时的整批保存会再写一遍。
        // 回滚更糟——用户看着颜色自己弹回去，却不知道是为什么。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.NoteService.SaveException = new IOException("文件被占用");

        await h.Vm.SetColorAsync(NoteColor.Orange);

        Assert.Equal(NoteColor.Orange, note.Color);

        string error = Assert.Single(h.Dialogs.ErrorRequests);

        Assert.StartsWith("管理器|改动没能写进文件，只留在内存里：", error, StringComparison.Ordinal);
        Assert.Contains(note.FilePath, error, StringComparison.Ordinal);
        Assert.Contains("文件被占用", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 菜单打开前会通知选中行变了()
    {
        // 颜色子菜单里那七项绑的是 SelectedNote.Color。它与 TopMostMenuHeader 不是
        // 同一条属性路径，所以只喊置顶那一句的话，右键一张蓝色的便签、在子菜单里点了「蓝」，
        // 那一项的勾会被 MenuItem 自己拨掉，而源没变、绑定不会去纠正它。
        // 菜单是挂在整个 ListBox 上的同一个实例，重开时也不会自己去重读。
        using var h = new ManagerHarness();

        var changed = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Vm.NotifyContextMenuOpening();

        Assert.Contains(nameof(ManagerViewModel.SelectedNote), changed);
    }

    // ================= 右键菜单：标签 =================

    [Fact]
    public async Task 编辑标签_初值带上当前标签_改完写回并落盘()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Tags.Add("工作");
        note.Tags.Add("紧急");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, _) => "工作, 私事";

        await h.Vm.EditTagsAsync();

        // 初值是便签当前那一串，用户改的通常是其中一两个，而不是从空开始重打。
        string[] request = Assert.Single(h.Dialogs.PromptRequests).Split('|');

        Assert.Equal("标签", request[0]);
        Assert.Equal("工作, 紧急", request[2]);

        // 拆开写而不是比较整个元组：元组里的列表是按引用比的，
        // 我在这儿新造的 List 与替身里记的那一份永远不是同一个对象。
        (Guid NoteId, IReadOnlyList<string> Tags) edit = Assert.Single(h.NoteService.TagsEdits);

        Assert.Equal(note.Id, edit.NoteId);
        Assert.Equal(["工作", "私事"], edit.Tags);

        Assert.Equal(new[] { note.Id }, h.NoteService.SavedNoteIds);
    }

    [Fact]
    public async Task 编辑标签_校验通过时回调给出null()
    {
        // 校验回调是调用方（这里）传进去的，替身把它跑出来的结论记下来。
        // 长度上限那条规则只活在这个回调里，不跑一遍就没地方验它。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, _) => "工作";

        await h.Vm.EditTagsAsync();

        Assert.Null(Assert.Single(h.Dialogs.PromptValidations));
    }

    [Fact]
    public async Task 编辑标签_取消时什么都不做()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Tags.Add("工作");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        // PromptResult 默认就是 null（取消）。
        await h.Vm.EditTagsAsync();

        Assert.Equal(["工作"], note.Tags);
        Assert.Empty(h.NoteService.TagsEdits);
        Assert.Empty(h.NoteService.SavedNoteIds);

        // 对话框根本没提交，校验回调不该跑。
        Assert.Empty(h.Dialogs.PromptValidations);
    }

    [Fact]
    public async Task 编辑标签_打开对话框却没改时什么都不做()
    {
        // 与点了当前那个颜色同理：不该白白刷新 UpdatedAt 让这一行跳走。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Tags.Add("工作");
        note.Tags.Add("紧急");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, initial) => initial;

        await h.Vm.EditTagsAsync();

        Assert.Empty(h.NoteService.TagsEdits);
        Assert.Empty(h.NoteService.SavedNoteIds);
        Assert.Equal(ManagerHarness.AtHours(0), note.UpdatedAt);
    }

    [Fact]
    public async Task 编辑标签_留空即清空全部标签()
    {
        // 「取消」与「输入了空串」是两件事：留空对标签编辑是有意义的。
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Tags.Add("工作");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, _) => "   ";

        await h.Vm.EditTagsAsync();

        Assert.Empty(note.Tags);
        Assert.Empty(Assert.Single(h.NoteService.TagsEdits).Tags);
    }

    [Fact]
    public async Task 编辑标签_超长标签被对话框拦下_改动不发生()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, _) => new string('x', TagRules.MaxLength + 1);

        await h.Vm.EditTagsAsync();

        string? error = Assert.Single(h.Dialogs.PromptValidations);

        Assert.NotNull(error);
        Assert.Contains(TagRules.MaxLength.ToString(CultureInfo.InvariantCulture), error, StringComparison.Ordinal);

        // 校验没过 = 对话框没提交，写入这一步压根不该发生。
        Assert.Empty(h.NoteService.TagsEdits);
        Assert.Empty(h.NoteService.SavedNoteIds);
    }

    [Fact]
    public async Task 编辑一张列表里已经没有的便签的标签时连对话框都不开()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Store.Remove(note.Id);

        await h.Vm.EditTagsAsync();

        Assert.Empty(h.Dialogs.PromptRequests);
        Assert.Empty(h.NoteService.TagsEdits);
    }

    [Fact]
    public async Task 编辑标签落盘失败时提示用户但不回滚内存()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        h.Add(note);
        h.Vm.Refresh();
        h.Vm.SelectedNote = h.Vm.Notes[0];

        h.Dialogs.PromptHandler = (_, _, _) => "工作";
        h.NoteService.SaveException = new UnauthorizedAccessException("没有写权限");

        await h.Vm.EditTagsAsync();

        Assert.Equal(["工作"], note.Tags);

        string error = Assert.Single(h.Dialogs.ErrorRequests);

        Assert.Contains("没有写权限", error, StringComparison.Ordinal);
    }

    // ================= 新建便签（§3.3 流 3） =================

    [Fact]
    public async Task 新建便签_开出一张窗口并把它列进列表()
    {
        using var h = new ManagerHarness();

        await h.Vm.NewNoteAsync();

        Note created = Assert.Single(h.Vm.Notes).Note;

        // 「点一下就有地方打字」是新建这个动作的全部意义，所以两件事都得发生：
        // 窗口开出来了，而且开的正是刚建的那一张——不是随便一张。
        Assert.Equal($"ShowNote({created.Id})", Assert.Single(h.Windows.Calls));
        Assert.Same(created, h.Windows.LastShownViewModel?.Note);

        // 初始正文为空（本轮裁决）。标题不在这里断言——它由 Note.Title 从正文派生，
        // 拿同一条派生规则反着验自己等于什么都没验。
        Assert.Equal(string.Empty, created.Content);
    }

    [Fact]
    public async Task 新建便签失败_提示用户而不是把异常抛出去()
    {
        using var h = new ManagerHarness();

        // 笔记目录没配、或者配的那个目录已经不在了（拔掉的移动盘）时，
        // 仓储抛的就是这个类型，§11.5 给的处置是「提示并引导去设置里重新选择目录」。
        h.NoteService.CreateNoteHandler = () => throw new InvalidOperationException("笔记目录不存在。");

        // 「不抛」是这条用例的全部要点。这条命令由按钮直接触发，
        // 异常逃出去就落到 §17.5 第一层，而那一层只能说出「程序遇到了一个问题」，
        // 说不出「笔记目录可在设置里更改」这句能让人照做的话。
        await h.Vm.NewNoteAsync();

        string error = Assert.Single(h.Dialogs.ErrorRequests);

        Assert.StartsWith("新建便签|", error, StringComparison.Ordinal);
        Assert.Contains("笔记目录不存在。", error, StringComparison.Ordinal);

        // 什么都没建出来：列表里不该凭空多一行，也不该开出一扇没有内容的空窗口。
        Assert.Empty(h.Vm.Notes);
        Assert.Empty(h.Windows.Calls);
    }

    // ================= 列表项上的标签与颜色 =================

    [Fact]
    public void 列表项把便签的标签与颜色原样交出来()
    {
        using var h = new ManagerHarness();
        var note = ManagerHarness.NewNote("# 笔记");

        note.Tags.Add("工作");
        note.Tags.Add("紧急");
        note.Color = NoteColor.Blue;

        h.Add(note);
        h.Vm.Refresh();

        var item = Assert.Single(h.Vm.Notes);

        // 不复制、不排序：标签的数量与顺序都是用户自己定的（§5.8 按 Front Matter 原样保留），
        // 重排会让他认不出自己写的那一串。
        Assert.Equal(new[] { "工作", "紧急" }, item.Tags);

        // 界面靠转换器把它换成笔刷，这里只保证这一头交出去的是颜色名。
        Assert.Equal(NoteColor.Blue, item.Color);
    }

    // ================= 别处改了便签集合 =================

    [Fact]
    public void 收到便签集合变更的消息会重扫列表()
    {
        // 回收站恢复走的就是这一条：管理器绑的列表是上一轮 Refresh() 拷出来的快照，
        // 而 NoteStore 不是可观察的——它自己无从知道别处多了一张便签。
        // 少了这条消息，用户恢复完得手动刷新才看得见（§18.2）。
        using var h = new ManagerHarness();

        h.Vm.Refresh();
        Assert.Empty(h.Vm.Notes);

        // 「别处」往 Store 里塞了一条，管理器毫无察觉。
        h.Add(ManagerHarness.NewNote("# 恢复回来的"));

        Assert.Empty(h.Vm.Notes);

        h.Messenger.Send(new NotesChangedMessage());

        // 计数与提示都跟着一起算了，不是只把那一行插进去。
        Assert.Single(h.Vm.Notes);
        Assert.Equal("1 条便签", h.Vm.CountText);
    }

    /// <summary>到这一刻为止一共开出了几张便签窗口。</summary>
    /// <remarks>
    /// 只数 <c>ShowNote</c>：窗口层上别的方法也可能被调到，而分批策略只管开窗那一步。
    /// </remarks>
    private static int ShownCount(ManagerHarness h) =>
        h.Windows.Calls.Count(call => call.StartsWith("ShowNote(", StringComparison.Ordinal));
}
