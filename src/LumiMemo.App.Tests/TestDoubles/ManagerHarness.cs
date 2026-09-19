using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Services;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;
using Microsoft.Extensions.Logging.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 一套装配好的 <see cref="ManagerViewModel"/> 与它的全部替身。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来是因为有两个地方要它：<c>ManagerViewModelTests</c> 测搜索接线的逻辑，
/// <c>ManagerWindowTests</c> 测那边的 XAML 能不能加载。后者需要的只是一个能塞进
/// <c>DataContext</c> 的真 ViewModel——若各造一份，两边的构造参数迟早会对不上，
/// 而症状是「窗口测试里那几个绑定的求值行为和生产代码不一样」这种看不出来的偏差。
/// </para>
/// <para>
/// <see cref="IDisposable"/> 是为了收掉两个定时器。真实资源没有，但"这个用例结束了"
/// 有个明确的落点，比靠 GC 强。
/// </para>
/// </remarks>
public sealed class ManagerHarness : IDisposable
{
    /// <summary>布局与搜索各用各的工厂。共用一个的话，"最后造出来的那个"会指错对象。</summary>
    private readonly RecordingUiTimerFactory _layoutTimers = new();

    public ManagerHarness()
    {
        Layout = new LayoutService(LayoutStore, new FixedDisplayProvider(), _layoutTimers);

        NoteService = new FakeNoteService();
        Windows = new RecordingWindowManager();

        // 真实现里这两步在 TrashService.MoveNoteToTrashAsync 里，替身不碰 Store。
        // 「删完之后列表里少一条」这条断言需要它真的发生，所以在装配处补上。
        NoteService.DeleteEffect = id =>
        {
            Store.Remove(id);
            Index.OnNoteRemoved(id);
        };

        // 同一个理由：真实现里「进 NoteStore、通知索引」是 NoteService.CreateNoteAsync
        // 自己的两步，替身不做。「新建之后列表里多一条」要靠它才成立。
        NoteService.CreateEffect = Add;

        Vm = new ManagerViewModel(
            Store,
            Index,
            Layout,
            NoteService,
            new NoteViewModelFactory(
                NoteService,
                new AutoSaveService(NoteService, new RecordingUiTimerFactory(), NullLogger<AutoSaveService>.Instance),
                new ImmediateDispatcher(),
                new RecordingDialogService(),
                Windows,
                Messenger),
            Windows,
            Presenter,
            Shell,
            Dialogs,
            Dispatcher,
            Clock,
            SearchTimers,
            Messenger);
    }

    /// <summary>
    /// 本套装配<strong>自己的一只</strong>消息总线，不是 <c>WeakReferenceMessenger.Default</c>。
    /// </summary>
    /// <remarks>
    /// 用全局那一只的话，同一进程里并跑的用例会互相收对方的
    /// <c>NotesChangedMessage</c>——表现是某条用例的列表被另一条用例的恢复操作刷了一遍，
    /// 而两条用例单独跑都绿。这类串台极难定位，隔离的代价只是一个字段。
    /// </remarks>
    public IMessenger Messenger { get; } = new WeakReferenceMessenger();

    public NoteStore Store { get; } = new();

    public SearchIndex Index { get; } = new();

    public InMemoryLayoutStore LayoutStore { get; } = new();

    public LayoutService Layout { get; }

    public RecordingUiTimerFactory SearchTimers { get; } = new();

    /// <summary>管理器那个去抖搜索用的定时器。</summary>
    public RecordingUiTimer SearchTimer => SearchTimers.Last;

    public RecordingWindowManager Windows { get; }

    /// <summary>管理器这一侧对外壳的调用（「在资源管理器中显示」）。</summary>
    /// <remarks>
    /// 与上面那个工厂里那只 <c>RecordingDialogService</c> <strong>刻意不是同一只</strong>：
    /// 那只记的是便签窗口的弹窗，这只记的是管理器的。共用一份的话，
    /// 「删不掉时提示了用户」和「便签窗口存不下时提示了用户」会落进同一个列表，
    /// 断言就分不清是谁弹的。
    /// </remarks>
    public RecordingDialogService Dialogs { get; } = new();

    public RecordingShellLauncher Shell { get; } = new();

    /// <summary>
    /// 喂给 <see cref="Vm"/> 的那只调度器。
    /// </summary>
    /// <remarks>
    /// 行为与 <see cref="ImmediateDispatcher"/> 完全一致，多出来的只是「每一次让帧都留个记号」，
    /// 分批开窗口（§17.1 第 11 步）靠它才验得动——只数最后开了几张窗口，
    /// 一个把二十扇窗一口气开完的实现也会给出同样的数字。
    /// </remarks>
    public RecordingDispatcher Dispatcher { get; } = new();

    /// <summary>
    /// 「把管理器窗口带出来」的记录型替身。
    /// </summary>
    /// <remarks>
    /// 用得着它的用例只有一条：一张便签都没打开时 <c>ShowAll</c> 会不会退回到管理器。
    /// 真去 <c>Show()</c> 一个窗口需要 STA 线程与消息泵，在测试进程里建不起来。
    /// </remarks>
    public RecordingManagerWindowPresenter Presenter { get; } = new();

    /// <summary>
    /// 拨到 <c>2026-09-19 12:00Z</c>。便签的时刻都取自同一套 <see cref="AtHours"/> 坐标，
    /// 于是"七天内 +30"那一档的落点是确定的，不会随机器上的真实时间漂。
    /// </summary>
    public FakeClock Clock { get; } = new(AtHours(12));

    public FakeNoteService NoteService { get; }

    public ManagerViewModel Vm { get; }

    /// <summary>加一张便签。Store 与索引必须一起更新，否则管理器会搜不到它。</summary>
    public void Add(Note note)
    {
        Store.Add(note);
        Index.OnNoteAdded(note);
    }

    /// <summary>把某张便签标成置顶。</summary>
    public void Pin(Guid noteId) => LayoutStore.GetOrCreate(noteId).IsTopMost = true;

    /// <summary>输入查询词并让去抖立即到期。</summary>
    public void SettleQuery(string query)
    {
        Vm.SearchQuery = query;
        SearchTimer.Fire();
    }

    /// <summary>2026-09-19 当天 0 点之后第 <paramref name="hours"/> 小时。</summary>
    public static DateTimeOffset AtHours(int hours) =>
        new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero).AddHours(hours);

    public static Note NewNote(string content, DateTimeOffset? updatedAt = null)
    {
        var id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.md",
            Content = content,
            CreatedAt = AtHours(0),
            UpdatedAt = updatedAt ?? AtHours(0),
        };
    }

    public void Dispose()
    {
        Layout.Dispose();
        SearchTimer.Dispose();
    }
}
