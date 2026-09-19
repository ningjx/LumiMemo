using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Messages;
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
/// <strong>本轮的范围</strong>：列表视图 + 搜索 + 右键菜单上的编辑动作
/// （置顶 / 颜色 / 标签 / 在资源管理器中显示 / 移入回收站）。
/// 三档视图切换按钮（列表/文件夹/标签）整个不出现，因为另外两档还没实现；
/// 画出来却点不动比没有更糟。
/// </para>
/// <para>
/// 改颜色的那一半是<strong>只是数据链路</strong>：写进 <c>Note</c>、落进 Front Matter、
/// 管理器这边立刻换颜色点。<strong>便签窗口还没跟着换肤</strong>——它眼下根本没画颜色，
/// 所以这里也不发消息（<c>NoteViewModel.Color</c> 那份镜像暂无人读）。这件事与
/// §15.3 调色板落地到便签窗口是同一批工作，等界面那一轮一起做。
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

    /// <summary>启动恢复时第一批开几张（§17.1 第 11 步的「第一帧打开前 3 个」）。</summary>
    private const int FirstBatchSize = 3;

    /// <summary>启动恢复时每批开几张（§17.1 第 11 步的「之后每帧再开 2 个」）。</summary>
    /// <remarks>
    /// 这两个数字来自文档，不是调出来的经验值。要动它们的话，先看清
    /// <see cref="RestoreOpenNotesAsync"/> 那条备注里「最后一批不让帧」的约定——
    /// 它决定了「开 N 张会让几次帧」，而测试钉的正是那串数字。
    /// </remarks>
    private const int SubsequentBatchSize = 2;

    private readonly NoteStore _store;
    private readonly SearchIndex _index;
    private readonly LayoutService _layoutService;
    private readonly INoteService _noteService;
    private readonly NoteViewModelFactory _viewModelFactory;
    private readonly IWindowManager _windowManager;
    private readonly IManagerWindowPresenter _managerWindow;
    private readonly IShellLauncher _shell;
    private readonly IDialogService _dialogs;
    private readonly IDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly IUiTimer _searchTimer;
    private readonly IMessenger _messenger;

    /// <summary>当前查询词下命中的<strong>全部</strong>便签，未截断。见 <see cref="Notes"/>。</summary>
    private readonly List<NoteListItem> _matches = [];

    public ManagerViewModel(
        NoteStore store,
        SearchIndex index,
        LayoutService layoutService,
        INoteService noteService,
        NoteViewModelFactory viewModelFactory,
        IWindowManager windowManager,
        IManagerWindowPresenter managerWindow,
        IShellLauncher shell,
        IDialogService dialogs,
        IDispatcher dispatcher,
        IClock clock,
        IUiTimerFactory timers,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(layoutService);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(managerWindow);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(messenger);

        _store = store;
        _index = index;
        _layoutService = layoutService;
        _noteService = noteService;
        _viewModelFactory = viewModelFactory;
        _windowManager = windowManager;
        _managerWindow = managerWindow;
        _shell = shell;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _clock = clock;
        _searchTimer = timers.Create();
        _messenger = messenger;

        // 自己改的（新建 / 删除 / 重扫）各方法直接调 Refresh()，不绕消息一圈。
        // 只有「别人改了便签集合、我无从知道」的那一处才需要这条线：
        // 回收站恢复（TrashViewModel.RestoreAsync）。见 NotesChangedMessage 的说明。
        //
        // 不做 Unregister：本类是 DI 单例，与进程同寿（App.xaml.cs 的注册处写着这一条），
        // 没有「先于消息源消失」的时刻。将来若改成非单例，这一条要跟着改。
        messenger.Register<NotesChangedMessage>(
            this,
            static (recipient, _) => ((ManagerViewModel)recipient).Refresh());
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

    // ------------------------------------------------------------------
    // 过滤条（§15.8）。三个条件之间是「与」：勾了置顶和最近 7 天就是两者同时满足。
    // ------------------------------------------------------------------

    /// <summary>只看置顶的便签。</summary>
    [ObservableProperty]
    private bool _filterTopMost;

    /// <summary>只看有标签的便签。</summary>
    [ObservableProperty]
    private bool _filterTagged;

    /// <summary>只看 <see cref="NoteSearch.RecentWindow"/>（七天）内改过的便签。</summary>
    [ObservableProperty]
    private bool _filterRecent;

    /// <summary>
    /// 三个勾选框一个都没勾，即过滤条上「全部」那一档。
    /// </summary>
    /// <remarks>
    /// 「全部」不是第四个条件、也不参与「与」运算——它就是<strong>没有条件</strong>。
    /// 做成一个真的复选框会立刻出现「全部 + 置顶」该是什么意思这种答不上来的问题。
    /// </remarks>
    public bool IsFilterAll => !HasFilter;

    private bool HasFilter => FilterTopMost || FilterTagged || FilterRecent;

    /// <summary>正在一次点掉多个过滤条件。<see cref="OnFilterChanged"/> 靠它跳过中间那几次。</summary>
    private bool _clearingFilters;

    partial void OnFilterTopMostChanged(bool value) => OnFilterChanged();

    partial void OnFilterTaggedChanged(bool value) => OnFilterChanged();

    partial void OnFilterRecentChanged(bool value) => OnFilterChanged();

    /// <summary>
    /// 把三个勾选框都清掉（过滤条上的「全部」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三个属性是一个一个赋的，每个 setter 都会走一次 <see cref="OnFilterChanged"/>，
    /// 也就是把列表整个重建三遍——点一下「全部」卡三下。中间那几次用
    /// <see cref="_clearingFilters"/> 挡掉，末尾自己刷新一遍。
    /// </para>
    /// <para>
    /// 这里<strong>不能</strong>图省事去直接写 <c>_filterTopMost</c> 那几个后备字段：
    /// 工具包把「绕过生成的属性直接碰后备字段」判成错误（MVVMTK0034），
    /// 理由很实在——那样连 <c>PropertyChanged</c> 都没有，
    /// 界面上那三个拨动按钮不会弹回来，用户会看着三个全部亮着、列表却是全部。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void ClearFilters()
    {
        if (!HasFilter)
        {
            return;
        }

        _clearingFilters = true;

        try
        {
            FilterTopMost = false;
            FilterTagged = false;
            FilterRecent = false;
        }
        finally
        {
            _clearingFilters = false;
        }

        OnFilterChanged();
    }

    /// <summary>列表为空时显示的提示。</summary>
    public string EmptyHint => (IsSearching, HasFilter) switch
    {
        (true, _) => "没有找到匹配的便签。",

        // 分开一句：文件夹里明明有便签却说"还没有便签"，用户会以为程序没扫到文件，
        // 而真正的原因是他自己勾了一个过滤条件。
        (false, true) => "没有符合筛选条件的便签。",
        _ => "这个文件夹里还没有便签。",
    };

    /// <summary>状态栏上的计数，形如「3 条便签」；搜索时是「找到 2 条」，只筛选时是「筛选出 2 条」。</summary>
    public string CountText => IsSearching
        ? $"找到 {_matches.Count} 条"
        : HasFilter
            ? $"筛选出 {_matches.Count} 条"
            : $"{_matches.Count} 条便签";

    /// <summary>结果超过渲染上限时状态栏上的提示。没超过时是空串。</summary>
    public string OverflowHint =>
        HasOverflow ? $"还有 {_matches.Count - MaxRenderedResults} 条结果，请细化搜索词" : string.Empty;

    /// <summary>命中条数是否超过了单次渲染上限。</summary>
    public bool HasOverflow => _matches.Count > MaxRenderedResults;

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>
    /// 过滤条件变了：重算列表，并让「全部」那一档跟着换外观。
    /// </summary>
    /// <remarks>
    /// <see cref="IsFilterAll"/> 不在 <see cref="Refresh"/> 的通知清单里（它跟列表内容无关），
    /// 所以它得自己在这儿喊一声。漏掉的症状是：勾上「置顶」之后「全部」还亮着，
    /// 看上去像是两个互相矛盾的条件同时生效。
    /// </remarks>
    private void OnFilterChanged()
    {
        // 「全部」是一次点掉三个条件的，中间那两次过渡态不该各刷一遍列表。
        // 末尾那一次是正常的逐个切换，会老老实实走到下面。
        if (_clearingFilters)
        {
            return;
        }

        OnPropertyChanged(nameof(IsFilterAll));

        Refresh();
    }

    partial void OnSelectedNoteChanged(NoteListItem? value) =>
        OnPropertyChanged(nameof(TopMostMenuHeader));

    /// <summary>右键菜单里那一项的标题：这张便签已经置顶时是「取消置顶」。</summary>
    /// <remarks>
    /// 让菜单写清"点下去会发生什么"，而不是永远写着「置顶」——后者在已经置顶的便签上
    /// 看不出这是「再置顶一次」（无动作）还是「取消置顶」。
    /// </remarks>
    public string TopMostMenuHeader =>
        SelectedNote is { } item && IsPinned(item.Id) ? "取消置顶" : "置顶";

    /// <summary>
    /// 菜单马上就要弹出来了：重算一遍菜单上那些跟当前状态有关的东西。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>光靠属性通知不够。</strong> 右键落在<em>已经选中</em>的那一行时
    /// <c>SelectedNote</c> 没有变，于是一声通知都不会发；而菜单是同一个共享实例
    /// （挂在整个 <c>ListBox</c> 上），里面的绑定不会因为菜单重开就自己重算——
    /// <c>PlacementTarget</c> 每次都指向同一个 <c>ListBox</c>，值没变，绑定就不重新求值。
    /// 表现为：用户先用便签标题条上的置顶按钮把某张便签置顶，再在管理器里右键它，
    /// 菜单上写着「置顶」。
    /// </para>
    /// <para>
    /// <strong>两声通知，各管一处。</strong>
    /// <see cref="TopMostMenuHeader"/> 就是那一项的绑定路径，得单独喊它；
    /// 颜色子菜单那七项绑的是 <c>SelectedNote.Color</c>，走的是<b>另一个</b>属性路径，
    /// 所以还要喊一声 <see cref="SelectedNote"/>。少了后一声的症状很具体：
    /// 用户右键一张蓝色的便签、在颜色子菜单里点了「蓝」（当前就是蓝的，什么都没发生），
    /// 那一项的勾会被 <c>MenuItem</c> 自己拨掉——源没变，绑定不会去纠正它。
    /// </para>
    /// </remarks>
    public void NotifyContextMenuOpening()
    {
        OnPropertyChanged(nameof(TopMostMenuHeader));
        OnPropertyChanged(nameof(SelectedNote));
    }

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
    /// 把一张便签移入回收站（§7.1）。列表项的右键菜单走这里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>三步的先后是有讲究的</strong>：补一次落盘 → 关窗 → 才搬文件。
    /// 关窗自己也会存一次（§17.3），但那一次<strong>靠不住</strong>：
    /// <c>NoteWindow.OnClosing</c> 的做法是「取消这次关闭、把保存排进消息队列、再关一次」，
    /// 于是 <c>CloseNote</c> 返回时那次保存还排在队列里没跑。此时若已经把文件搬进回收站，
    /// 等它跑起来便签已不在 <c>NoteStore</c> 里，<c>SaveNoteAsync</c> 会静默返回
    /// （那是它刻意为之的行为）——用户最后半秒敲的字既没进文件也没进回收站，
    /// 而他从回收站恢复出来的是一份旧内容。所以这里主动补一次保存：
    /// <strong>此刻便签还在 Store 里，写得进去</strong>。
    /// </para>
    /// <para>
    /// 窗口没开着时不必补。那种情况下内存里的内容与磁盘上的一致——
    /// 编辑只可能来自一个开着的窗口，而它在关掉时已经存过了。
    /// </para>
    /// <para>
    /// <strong>不弹确认对话框</strong>：进回收站可逆（§7.2 起能恢复），
    /// 与「清空回收站」（§7.4，<c>ConfirmAsync</c> 的用武之地）不是一回事。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task DeleteNoteAsync(NoteListItem? item)
    {
        if (item is null || !_store.Contains(item.Id))
        {
            // 列表是上一轮的快照，这一条已经不在了（多半是别处刚删过）。
            // 静默返回即可——用户再刷新一次列表就对了，与 OpenNote 的处理一致。
            return;
        }

        if (_windowManager.IsNoteOpen(item.Id))
        {
            await _noteService.SaveNoteAsync(item.Id);

            _windowManager.CloseNote(item.Id);
        }

        await _noteService.DeleteNoteAsync(item.Id);

        // 重扫列表而不是把那一行摘掉：计数、空列表提示、溢出提示全挂在 Refresh 里，
        // 只摘一行的话那三处都要各自维护一遍。
        Refresh();
    }

    /// <summary>
    /// 切换一张便签的置顶（§15.8 右键菜单）。列表项右键菜单走这里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 置顶的真实状态在 <see cref="NoteLayout.IsTopMost"/> 上（落盘到 <c>layout.json</c>，
    /// §18.1 的三类状态里它属于窗口状态那一类），所以这里写布局层而不是便签模型。
    /// </para>
    /// <para>
    /// <strong>写完必须喊一声</strong>：开着的便签窗口自己存了一份镜像
    /// （<c>NoteViewModel.IsTopMost</c>）。镜像的改动会往下传到窗口，但布局层这一侧
    /// 不会发出任何通知——不喊这一声，窗口既不真的置顶、标题条上那个按钮也还显示着旧状态，
    /// 用户再点一下反而把它取消了。见 <see cref="NoteTopMostChangedMessage"/>。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void ToggleTopMost(NoteListItem? item)
    {
        if (item is null || !_store.Contains(item.Id))
        {
            return;
        }

        NoteLayout layout = _layoutService.GetOrCreate(item.Id);

        layout.IsTopMost = !layout.IsTopMost;

        // GetOrCreate 自己也会安排一次落盘，但那一句管的是「新条目要落盘」。
        // 这一句管的是「这一个字段变了」——两件事，将来 GetOrCreate 改成
        // 只在新建时才标脏的那天，这一句还在。
        _layoutService.MarkDirtyAndScheduleFlush();
        _messenger.Send(new NoteTopMostChangedMessage(item.Id, layout.IsTopMost));

        // 列表要重算：搜索结果里置顶的排前面（§12.2 的 +50），
        // 菜单上那一句说法也跟着换了（TopMostMenuHeader）。
        Refresh();
    }

    /// <summary>
    /// 在资源管理器里打开这张便签所在的位置并选中它（§15.8 右键菜单）。
    /// </summary>
    /// <remarks>
    /// 失败时给一句提示而不是静默返回（与 <see cref="SettingsViewModel.OpenNotesFolderAsync"/>
    /// 同一手法）。这里的失败只有一个实际来由：文件已经不在了，而列表还是上一轮的快照
    /// （外部删掉、或者用户在另一台机器上删的）。用户刚点了一下按钮，
    /// 什么都不发生的话他只会以为程序卡住了。
    /// </remarks>
    [RelayCommand]
    public async Task RevealInExplorerAsync(NoteListItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_shell.RevealInExplorer(item.Note.FilePath))
        {
            await _dialogs.ShowErrorAsync("管理器", $"找不到这个文件：\n{item.Note.FilePath}");
        }
    }

    /// <summary>
    /// 改当前选中那张便签的颜色（§15.8 右键菜单的「颜色」子菜单）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>为什么参数只有颜色、便签取 <see cref="SelectedNote"/>。</strong>
    /// 子菜单里那七项各有各的 <c>CommandParameter</c>（颜色），而便签也是必需的一个参数——
    /// 命令的两个参数写法（工具包会生成 <c>ICommand&lt;(T1, T2)&gt;</c>）要求 XAML 能造出一个元组，
    /// 而 XAML 造不出来。这里之所以能安全地退回读选中项：菜单只可能在<strong>某一行上</strong>
    /// 弹出来，而 <c>ManagerWindow.OnListContextMenuOpening</c> 在弹菜单之前一定先把那一行选上，
    /// 所以命令跑起来时 <see cref="SelectedNote"/> 必然就是被右键的那一张。
    /// </para>
    /// <para>
    /// <strong>点了当前这一支就什么都不做。</strong> 白白往下走一次的话，
    /// <c>ApplyColorEdit</c> 会刷新 <c>UpdatedAt</c>，于是列表按修改时间重排——
    /// 用户只是点了一下「确认还是这个颜色」，却看到这一行跳到别处去了。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task SetColorAsync(NoteColor color)
    {
        if (SelectedNote is not { } item || !_store.Contains(item.Id) || item.Note.Color == color)
        {
            return;
        }

        _noteService.ApplyColorEdit(item.Note, color);

        await PersistEditAsync(item);
    }

    /// <summary>
    /// 编辑当前选中那张便签的标签（§15.8 右键菜单的「标签」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 输入框里是「逗号分隔的一大串」，不是一列可增删的 chip：标签数量少，用户改起来是
    /// 「换掉整批」而不是「逐个调」，一次打完比来回点便宜；chip 那种形态还要多一个自绘控件
    /// 与一套增删逻辑。
    /// </para>
    /// <para>
    /// 长度上限的校验交给对话框（<c>validate</c>）而不是等它关掉之后再弹一个错误框——
    /// 那样用户刚打的一串就没了。<strong>校验与写入必须是同一次拆分</strong>：
    /// 两处各拆一遍的话，将来给 <c>TagRules.Separators</c> 加一个分隔符，就会出现
    /// 「校验说有问题的那个标签，写入时其实已经被拆没了」。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task EditTagsAsync()
    {
        if (SelectedNote is not { } item || !_store.Contains(item.Id))
        {
            return;
        }

        string? input = await _dialogs.PromptAsync(
            "标签",
            "多个标签用逗号隔开。开头的 # 会被去掉，标签里的空格会换成 -。留空即清空全部标签。",
            string.Join(", ", item.Note.Tags),
            ValidateTagsInput);

        // 取消。注意不能拿「输入为空串」兼任取消——留空是有意义的（清空标签）。
        if (input is null)
        {
            return;
        }

        List<string> tags = TagRules.Split(input);

        if (item.Note.Tags.SequenceEqual(tags, StringComparer.Ordinal))
        {
            // 打开了对话框却没改：与 SetColor 点了当前那一支同理，不该白白刷新 UpdatedAt。
            return;
        }

        _noteService.ApplyTagsEdit(item.Note, tags);

        await PersistEditAsync(item);
    }

    /// <summary>标签输入框的校验（§5.8 的长度上限）。返回错误文案，<c>null</c> 表示可以收下。</summary>
    private static string? ValidateTagsInput(string input)
    {
        foreach (string tag in TagRules.Split(input))
        {
            if (tag.Length > TagRules.MaxLength)
            {
                return $"标签「{tag}」有 {tag.Length} 个字，超过上限 {TagRules.MaxLength} 个，请改短一些。";
            }
        }

        return null;
    }

    /// <summary>
    /// 把刚改好的内存状态写进文件，然后重建列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>直接存，不走 <c>AutoSaveService</c> 的去抖。</strong> 去抖是为「连续按键」
    /// 准备的（§11.1），而这里是一次点完就结束的动作；用户改完颜色随即关掉程序，
    /// 那几百毫秒的等待就成了纯粹的丢数据窗口。<c>SaveNoteAsync</c> 里面还有一道
    /// 「内容哈希没变就不写盘」的自检（§5.9），所以这里也不会白白多写文件。
    /// </para>
    /// <para>
    /// 失败时<strong>报一句，但不回滚内存</strong>——与 §11.5 的策略一致：用户改的东西还在，
    /// 下一次改动或退出时的整批保存会再写一遍。回滚反而更糟：用户看着颜色自己弹回去，
    /// 却不知道是为什么。catch 的这两个类型与 <c>AutoSaveService</c> 那条一致
    /// （文件被别的程序占用、没有写权限）。
    /// </para>
    /// </remarks>
    private async Task PersistEditAsync(NoteListItem item)
    {
        try
        {
            await _noteService.SaveNoteAsync(item.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(
                "管理器",
                $"改动没能写进文件，只留在内存里：\n{item.Note.FilePath}\n\n{ex.Message}");
        }

        Refresh();
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
    /// <para>
    /// 这里必须自己接住异常。「新建」是用户点一下就要有反应的动作，而它一次要动三样
    /// 会失败的东西：笔记目录（可能未配置、可能随移动盘一起没了）、文件名分配（§5.6）、
    /// 首次写盘（可能没权限、可能被占用）。裸抛出去就是 §17.5 那一层的活，
    /// 但用户看到的会是一个「程序出错了」的框，而这里能告诉他到底哪一步不行、该怎么办。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task NewNoteAsync()
    {
        try
        {
            Note note = await _noteService.CreateNoteAsync();

            ShowNote(note, _layoutService.GetOrCreate(note.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync(
                "新建便签",
                $"没能建出这张便签：\n{ex.Message}\n\n笔记目录可在设置里更改。");
        }

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
    /// <para>
    /// <strong>一张都没得亮时退回到管理器窗口</strong>（最后一小段）。
    /// §17.6 那三步只覆盖了「有便签可显示」的情形，而这条路有两个入口是用户换不掉的：
    /// 双击托盘图标（§15.9 固定走它）与再启动一个实例（§17.2 的原话是「通知它把自己带到前台」）。
    /// 若用户把每张便签都点过 ✕，程序就一个界面都没有了，那时点桌面图标毫无反应，
    /// 在他眼里与「程序坏了」没有区别。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void ShowAll()
    {
        _dispatcher.VerifyAccess();

        List<NoteOpenRequest> requests = [.. _noteService.OpenAll()];

        foreach (NoteOpenRequest request in requests)
        {
            ShowNote(request.Note, request.Layout);
        }

        _windowManager.ShowAllNotes();

        // 判据取 OpenAll() 的结果，而不是去问窗口层有几个窗口：§17.6 说
        // 「OpenAll() 决定『应该有哪些窗口』」，它为空就是「没有该显示的」。
        // 「窗口存在 ⟺ IsOpen 为真」是业务层与窗口层之间那条不变式，两边是同一件事。
        if (requests.Count == 0)
        {
            _managerWindow.BringToFront();
        }
    }

    /// <summary>§17.1 第 11 步：把上次退出时开着的便签重新开出来，分批进行。返回一共开了几张。</summary>
    /// <remarks>
    /// <para>
    /// 「上次开着」完全由 <c>layout.json</c> 里的 <c>isOpen</c> 决定，判据只有一个——
    /// 与 <see cref="ShowAll"/> 读的是同一个 <c>OpenAll()</c>。
    /// <strong>因此不需要判断「这是不是第一次启动」</strong>：首启时没有布局档，
    /// <c>OpenAll()</c> 自然返回空，这个方法就什么都不做，§17.1 的「首次启动不自动弹便签」
    /// 因此不需要一条专门的规则。两件事共用一条判据，就不会出现「首启弹了」
    /// 或「重开不恢复」这种一半对一半错的状态。
    /// </para>
    /// <para>
    /// <strong>分批是必需的，不是优化</strong>（§17.1 要点）：一次开二十扇窗，每扇都要造
    /// ViewModel、建 HWND、走布局，合起来会让第一帧卡住几百毫秒。所以第一批
    /// <see cref="FirstBatchSize"/> 个，之后每批 <see cref="SubsequentBatchSize"/> 个，
    /// 批次之间把控制权交还给消息泵。
    /// </para>
    /// <para>
    /// 交还用的是 <c>YieldAsync</c>（<c>DispatcherPriority.Background</c>），
    /// <strong>不是 <c>InvokeAsync</c></strong>：后者排在 <c>Normal</c>，比重绘所在的
    /// <c>Render</c> 还高，下一批会抢在重绘前面执行，界面照样卡。细节见 <c>IDispatcher.YieldAsync</c> 的说明。
    /// </para>
    /// <para>
    /// <strong>最后一批开完不再让帧</strong>：那时已经无事可做，白让一帧只会让调用方多等一轮消息泵。
    /// 于是「开 N 张会让几次帧」的答案是 <c>ceil((N-3)/2)</c> 与 <c>0</c> 取大——
    /// 三张以内一次都不让，七张让两次（3 与 5 那两个点上）。
    /// </para>
    /// <para>
    /// 不 <c>ConfigureAwait(false)</c>：每一批都要开窗，而开窗必须在 UI 线程上（§3.4 规则 T1）。
    /// </para>
    /// </remarks>
    public async Task<int> RestoreOpenNotesAsync()
    {
        _dispatcher.VerifyAccess();

        List<NoteOpenRequest> requests = [.. _noteService.OpenAll()];

        int opened = 0;

        while (opened < requests.Count)
        {
            int batch = opened == 0 ? FirstBatchSize : SubsequentBatchSize;
            int end = Math.Min(opened + batch, requests.Count);

            for (; opened < end; opened++)
            {
                ShowNote(requests[opened].Note, requests[opened].Layout);
            }

            if (opened < requests.Count)
            {
                await _dispatcher.YieldAsync();
            }
        }

        return opened;
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

    /// <summary>按当前查询词与过滤条件算出要展示的行。</summary>
    /// <remarks>
    /// <strong>过滤在搜索之前</strong>：条件筛掉的那些便签压根不该参与评分与排序，
    /// 也不该进 <c>_matches</c>——状态栏那个「筛选出 N 条」与「还有 N 条结果」
    /// 说的都是筛过之后的数，而它们读的正是 <c>_matches</c>。
    /// </remarks>
    private List<NoteListItem> BuildItems()
    {
        IReadOnlyList<Note> notes = _store.Snapshot();

        // 置顶这一份查两次（过滤一次、评分一次），所以只建一次。
        HashSet<Guid> topMost = TopMostIds();

        if (HasFilter)
        {
            notes = [.. notes.Where(note => PassesFilter(note, topMost))];
        }

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
                .Search(notes, query, _index.GetPlainText, topMost, _clock.Now)
                .Select(hit => new NoteListItem(hit.Note, _index.GetPlainText(hit.Note.Id), query))
        ];
    }

    /// <summary>这张便签是否满足当前勾选的<strong>全部</strong>过滤条件（§15.8 的「与」关系）。</summary>
    private bool PassesFilter(Note note, HashSet<Guid> topMostIds)
    {
        if (FilterTopMost && !topMostIds.Contains(note.Id))
        {
            return false;
        }

        if (FilterTagged && note.Tags.Count == 0)
        {
            return false;
        }

        // 与 §12.2 的「七天内 +30」共用同一个窗口常量，见 NoteSearch.RecentWindow。
        // 用 >= 那一侧不判：未来时间戳（时钟回拨）算"最近"，与评分那边一致。
        return !FilterRecent || _clock.Now - note.UpdatedAt <= NoteSearch.RecentWindow;
    }

    /// <summary>这张便签现在是否置顶。</summary>
    private bool IsPinned(Guid noteId)
    {
        foreach (NoteLayout layout in _layoutService.All)
        {
            if (layout.NoteId == noteId)
            {
                return layout.IsTopMost;
            }
        }

        return false;
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
