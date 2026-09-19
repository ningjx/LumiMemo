using LumiMemo.App.Tests.TestDoubles;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
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
}
