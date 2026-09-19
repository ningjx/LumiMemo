using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using LumiMemo.Core.Services;
using LumiMemo.Core.Stores;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 管理器窗口（程序的主界面）的界面状态（§15.8）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>本轮的范围</strong>：列表视图 + 搜索。把便签列出来、按查询词筛并排序、
/// 双击开一张、批量显示/隐藏。三档视图切换按钮（列表/文件夹/标签）整个不出现，
/// 因为另外两档还没实现；画出来却点不动比没有更糟。
/// </para>
/// <para>
/// <strong>搜索的两条路径不能合并</strong>：查询词为空时直接取
/// <see cref="NoteStore.Snapshot"/> 按修改时间倒序（<see cref="NoteSearch.OrderForList"/>），
/// 有查询词时才走 <see cref="NoteSearch.Search"/> 的评分排序。让空查询也去走评分，
/// 那些「分数很低但确实匹配」的规则会把整个列表重排一遍，
/// 用户会在清空输入框的瞬间看到列表乱跳（§12.1）。
/// </para>
/// <para>
/// 它<strong>不持有窗口引用</strong>（§18.3），开窗走 <see cref="IWindowManager"/>。
/// </para>
/// </remarks>
public sealed partial class ManagerViewModel : ObservableObject
{
    /// <summary>单次渲染的结果上限（§15.8）。</summary>
    /// <remarks>
    /// 搜一个常见字可能命中上千条。全部渲染会让面板卡住，而用户也不会去看第 500 条。
    /// 先给 200 条，超出的部分改成一句「请细化搜索词」。
    /// </remarks>
    public const int MaxRenderedResults = 200;

    private readonly NoteStore _store;
    private readonly SearchIndex _index;
    private readonly LayoutService _layoutService;
    private readonly INoteService _noteService;
    private readonly NoteViewModelFactory _viewModelFactory;
    private readonly IWindowManager _windowManager;
    private readonly IDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly IUiTimer _searchTimer;

    /// <summary>当前查询词下命中的<strong>全部</strong>便签，未截断。见 <see cref="Notes"/>。</summary>
    private readonly List<NoteListItem> _matches = [];

    public ManagerViewModel(
        NoteStore store,
        SearchIndex index,
        LayoutService layoutService,
        INoteService noteService,
        NoteViewModelFactory viewModelFactory,
        IWindowManager windowManager,
        IDispatcher dispatcher,
        IClock clock,
        IUiTimerFactory timers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(layoutService);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);

        _store = store;
        _index = index;
        _layoutService = layoutService;
        _noteService = noteService;
        _viewModelFactory = viewModelFactory;
        _windowManager = windowManager;
        _dispatcher = dispatcher;
        _clock = clock;
        _searchTimer = timers.Create();
    }

    /// <summary>窗口标题。</summary>
    public string Title => "LumiMemo";

    /// <summary>列表里的便签。至多 <see cref="MaxRenderedResults"/> 条，见 <see cref="OverflowHint"/>。</summary>
    public ObservableCollection<NoteListItem> Notes { get; } = [];

    /// <summary>当前选中的行。批量操作作用在它上面。</summary>
    [ObservableProperty]
    private NoteListItem? _selectedNote;

    /// <summary>
    /// 搜索框里的文字。界面必须用 <c>UpdateSourceTrigger=PropertyChanged</c> 绑定它，
    /// 否则要等到焦点离开才更新，去抖就成了摆设。
    /// </summary>
    /// <remarks>
    /// 初值是<strong>空串而不是 <c>null</c></strong>：它绑在 <c>TextBox.Text</c> 上，
    /// 而那个属性永远给不出 <c>null</c>。若初值是 <c>null</c>，
    /// "空"就有了两种表示，占位提示的可见性判断必须在两处各写一遍。
    /// </remarks>
    [ObservableProperty]
    private string? _searchQuery = string.Empty;

    /// <summary>
    /// 搜索输入停止多久之后执行（§15.8）。由启动序列从 <c>AppSettings.SearchDebounceMs</c> 灌进来。
    /// </summary>
    /// <remarks>
    /// 做成可写属性而不是构造参数，与 <c>AutoSaveService.DelayMilliseconds</c>、
    /// <c>WindowManager.RestoreAfterShowDesktop</c> 同一手法：设置是启动时才知道的，
    /// 而它不该成为构造函数的一部分——那样测试里每造一个 ViewModel 都要先造一份设置。
    /// </remarks>
    public int SearchDebounceMilliseconds { get; set; } = 150;

    /// <summary>列表为空时显示的提示。</summary>
    public string EmptyHint =>
        IsSearching ? "没有找到匹配的便签。" : "这个文件夹里还没有便签。";

    /// <summary>状态栏上的计数，形如「3 条便签」，搜索时是「找到 2 条」。</summary>
    public string CountText => IsSearching ? $"找到 {_matches.Count} 条" : $"{_matches.Count} 条便签";

    /// <summary>结果超过渲染上限时状态栏上的提示。没超过时是空串。</summary>
    public string OverflowHint =>
        HasOverflow ? $"还有 {_matches.Count - MaxRenderedResults} 条结果，请细化搜索词" : string.Empty;

    /// <summary>命中条数是否超过了单次渲染上限。</summary>
    public bool HasOverflow => _matches.Count > MaxRenderedResults;

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>
    /// 查询词变了：重新起一次去抖计时（§15.8 的「输入停止 150ms 后执行」）。
    /// </summary>
    /// <remarks>
    /// <strong>重新计时而不是排队。</strong> 连打五个字只该搜最后一次；
    /// 若做成排队，用户打完一个词要眼睁睁看着列表按五个中间状态依次抖过去。
    /// <see cref="IUiTimer.Start"/> 的语义正是「从本次调用算起」。
    /// </remarks>
    partial void OnSearchQueryChanged(string? value)
    {
        _searchTimer.Start(TimeSpan.FromMilliseconds(SearchDebounceMilliseconds), Refresh);
    }

    /// <summary>
    /// 按当前查询词重建列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 整体重建而不是增量更新。增量更新需要把「哪张便签的哪个字段变了」这件事
    /// 从业务层一路传到列表，而排序键是修改时间——内容一变顺序就可能变。
    /// 便签数量是几十这个量级，重建一次的代价可以忽略。
    /// </para>
    /// <para>
    /// <see cref="IDispatcher.VerifyAccess"/> 不是装饰：<see cref="ObservableCollection{T}"/>
    /// 被 UI 绑定时跨线程改会抛异常或错乱（§3.4 规则 T5）。
    /// 调用方一个在启动序列（UI 线程）、一个在去抖定时器（生产实现是
    /// <c>DispatcherTimer</c>，本来就在 UI 线程），但将来文件监听接上后不一定——
    /// 那时候这行会立刻抓出来。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void Refresh()
    {
        _dispatcher.VerifyAccess();

        // 手动刷新时撤掉还没到期的去抖：它再跑一次只会得出同一个结果。
        _searchTimer.Stop();

        _matches.Clear();
        _matches.AddRange(BuildItems());

        Notes.Clear();

        foreach (NoteListItem item in _matches.Take(MaxRenderedResults))
        {
            Notes.Add(item);
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(OverflowHint));
    }

    /// <summary>
    /// 开一张便签的窗口。列表项双击走这里。
    /// </summary>
    /// <remarks>
    /// 三步的分工是 §3.3 流 3 的原文：业务层判断「这张便签还在不在、该不该置 IsOpen」，
    /// 工厂造 ViewModel，窗口管理层开窗。任一步都不许越界——
    /// 尤其不能在 ViewModel 里 <c>new NoteWindow()</c>，那会让整个界面层无法测试。
    /// </remarks>
    [RelayCommand]
    public void OpenNote(NoteListItem? item)
    {
        if (item is null)
        {
            return;
        }

        NoteOpenRequest? request = _noteService.OpenNote(item.Id);

        // 便签在内存里不存在。列表是上一轮的快照，来不及刷新时会出现这种情况，
        // 静默返回即可——用户再点一次「刷新」列表就对了。
        if (request is null)
        {
            return;
        }

        ShowNote(request.Value.Note, request.Value.Layout);
    }

    /// <summary>
    /// 新建一张便签并立刻打开它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 托盘菜单、将来的 <c>Ctrl+N</c>、以及管理器工具条上那个 <c>[ + 新建 ]</c>
    /// 走的是同一条路（§17.6 对「显示全部」的要求同理：入口可以多，路径只能有一条）。
    /// </para>
    /// <para>
    /// 顺序是 §3.3 流 3 的：业务层建（<c>CreateNoteAsync</c> 落盘并登记进 <c>NoteStore</c>）→
    /// 布局层给一个位置（<c>GetOrCreate</c>，§17.2 的算位在里面）→ 工厂造 ViewModel → 开窗。
    /// <strong>不能在 ViewModel 里 <c>new NoteWindow()</c></strong>，那样整个界面层就没法测了。
    /// </para>
    /// <para>
    /// 全程<strong>不 <c>ConfigureAwait(false)</c></strong>：调用方在 UI 线程上，
    /// 而 <c>Refresh()</c> 要改被绑定的 <c>ObservableCollection</c>（§3.4 规则 T5）。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task NewNoteAsync()
    {
        Note note = await _noteService.CreateNoteAsync();

        ShowNote(note, _layoutService.GetOrCreate(note.Id));

        Refresh();
    }

    /// <summary>
    /// 把该打开的便签全部打开，再把已经开着的窗口统一亮出来（§17.6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>这是四个入口共用的那一条路径</strong>：托盘菜单项、双击托盘图标、
    /// 第二个实例启动时的通知、以及将来 <c>Ctrl+Alt+N</c> 热键，四者都走这里。
    /// 各写一份的话，「用户关掉某张便签后它还会不会回来」这种判断迟早会在某一条路上走岔，
    /// 而那种偏差只在特定入口下出现，极难复现。
    /// </para>
    /// <para>
    /// <strong>两步的职责不能混</strong>（§14.2）：<c>OpenAll()</c> 决定「应该有哪些窗口」，
    /// <c>ShowAllNotes()</c> 只负责「把已经有的窗口亮出来并激活第一个」。
    /// 前者不做后者的事，后者也不知道该显示哪些便签。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void ShowAll()
    {
        _dispatcher.VerifyAccess();

        foreach (NoteOpenRequest request in _noteService.OpenAll())
        {
            ShowNote(request.Note, request.Layout);
        }

        _windowManager.ShowAllNotes();
    }

    /// <summary>把所有便签窗口收起来（不改变数据，也不写 layout）。</summary>
    [RelayCommand]
    public void HideAll() => _windowManager.HideAllNotes();

    /// <summary>
    /// 重新扫描笔记目录（§10.5），网络盘用户的逃生出口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本轮的 <c>ApplyExternalChange</c> 还是 <c>NotSupportedException</c>——
    /// 文件监听整体推迟了。于是「在别的编辑器里改了 .md」这件事唯一的感知方式
    /// 就是用户自己点这一下，它<strong>不是</strong>可有可无的兜底。
    /// </para>
    /// <para>
    /// <strong>它不关掉已经打开的便签窗口。</strong> 重扫会重建 <c>NoteStore</c> 里的对象，
    /// 而开着的窗口各持一份自己的 <c>Note</c> 引用——这是已经存在的取舍（§3.3 流 2
    /// 本该怎么处理还没有定论），本轮不在这里解决。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task ReloadAllAsync()
    {
        await _noteService.LoadAllAsync();

        Refresh();
    }

    /// <summary>开一张便签的窗口：工厂造 ViewModel，窗口管理层开窗。</summary>
    private void ShowNote(Note note, NoteLayout layout)
    {
        NoteViewModel viewModel = _viewModelFactory.Create(note, layout);

        _windowManager.ShowNote(viewModel, layout);
    }

    /// <summary>按当前查询词算出要展示的行。</summary>
    private List<NoteListItem> BuildItems()
    {
        IReadOnlyList<Note> notes = _store.Snapshot();

        if (!IsSearching)
        {
            return [.. NoteSearch.OrderForList(notes)
                                 .Select(note => new NoteListItem(note, _index.GetPlainText(note.Id)))];
        }

        // 查询词已经在 Search 里裁过空白，这里再裁一次只是为了把同一个词交给 NoteListItem
        // ——留着两端的空格会让它拿 " docker " 去 IndexOf，摘要就永远摘不出来。
        string query = SearchQuery!.Trim();

        return
        [
            .. NoteSearch
                .Search(notes, query, _index.GetPlainText, TopMostIds(), _clock.Now)
                .Select(hit => new NoteListItem(hit.Note, _index.GetPlainText(hit.Note.Id), query))
        ];
    }

    /// <summary>当前处于置顶的便签 id，交给 §12.2 的「置顶 +50」。</summary>
    private HashSet<Guid> TopMostIds()
    {
        var ids = new HashSet<Guid>();

        foreach (NoteLayout layout in _layoutService.All)
        {
            if (layout.IsTopMost)
            {
                ids.Add(layout.NoteId);
            }
        }

        return ids;
    }
}
