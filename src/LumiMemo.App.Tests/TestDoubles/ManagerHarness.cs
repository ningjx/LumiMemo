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
                new WeakReferenceMessenger()),
            Windows,
            new ImmediateDispatcher(),
            Clock,
            SearchTimers);
    }

    public NoteStore Store { get; } = new();

    public SearchIndex Index { get; } = new();

    public InMemoryLayoutStore LayoutStore { get; } = new();

    public LayoutService Layout { get; }

    public RecordingUiTimerFactory SearchTimers { get; } = new();

    /// <summary>管理器那个去抖搜索用的定时器。</summary>
    public RecordingUiTimer SearchTimer => SearchTimers.Last;

    public RecordingWindowManager Windows { get; }

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
