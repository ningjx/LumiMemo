using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Stores;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 管理器窗口（程序的主界面）的界面状态（§15.8）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>本轮的范围</strong>：只做列表视图——把笔记目录里的便签按修改时间倒序列出来、
/// 双击开一张、批量显示/隐藏。三档视图切换按钮（列表/文件夹/标签）整个不出现，
/// 因为另外两档还没实现；画出来却点不动比没有更糟。
/// </para>
/// <para>
/// 搜索、排序控件、卡片副标题按查询词切换属于阶段 6/7，等 <c>SearchIndex.Search</c> 接上后再补。
/// 在那之前 <see cref="NoteListItem.Subtitle"/> 恒为「时间 · 字数」。
/// </para>
/// <para>
/// 它<strong>不持有窗口引用</strong>（§18.3），开窗走 <see cref="IWindowManager"/>。
/// </para>
/// </remarks>
public sealed partial class ManagerViewModel : ObservableObject
{
    private readonly NoteStore _store;
    private readonly INoteService _noteService;
    private readonly NoteViewModelFactory _viewModelFactory;
    private readonly IWindowManager _windowManager;
    private readonly IDispatcher _dispatcher;

    public ManagerViewModel(
        NoteStore store,
        INoteService noteService,
        NoteViewModelFactory viewModelFactory,
        IWindowManager windowManager,
        IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _store = store;
        _noteService = noteService;
        _viewModelFactory = viewModelFactory;
        _windowManager = windowManager;
        _dispatcher = dispatcher;
    }

    /// <summary>窗口标题。</summary>
    public string Title => "LumiMemo";

    /// <summary>列表里的便签，按修改时间倒序。</summary>
    public ObservableCollection<NoteListItem> Notes { get; } = [];

    /// <summary>当前选中的行。批量操作作用在它上面。</summary>
    [ObservableProperty]
    private NoteListItem? _selectedNote;

    /// <summary>列表为空时显示的提示。笔记目录选好了但里面一条便签都没有时会看到它。</summary>
    public string EmptyHint => "这个文件夹里还没有便签。";

    /// <summary>状态栏上的计数，形如「3 条便签」。</summary>
    public string CountText => $"{Notes.Count} 条便签";

    /// <summary>
    /// 按 <see cref="NoteStore"/> 的当前内容重建列表。
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
    /// 本轮调用方都在 UI 线程上，但将来文件监听接上后不一定——那时候这行会立刻抓出来。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void Refresh()
    {
        _dispatcher.VerifyAccess();

        Notes.Clear();

        foreach (Note note in _store.Snapshot().OrderByDescending(note => note.UpdatedAt))
        {
            Notes.Add(new NoteListItem(note));
        }

        OnPropertyChanged(nameof(CountText));
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

        NoteViewModel viewModel = _viewModelFactory.Create(request.Value.Note, request.Value.Layout);

        _windowManager.ShowNote(viewModel, request.Value.Layout);
    }

    /// <summary>把所有便签窗口叫出来（被最小化的还原）。</summary>
    /// <remarks>
    /// 只显示<strong>已经有窗口</strong>的那些。哪些便签应该打开是业务状态（<c>IsOpen</c>）决定的，
    /// 本轮托盘菜单的「显示全部」也走这里——真正的「按 IsOpen 恢复」在启动序列里做。
    /// </remarks>
    [RelayCommand]
    public void ShowAll() => _windowManager.ShowAllNotes();

    /// <summary>把所有便签窗口收起来（不改变数据，也不写 layout）。</summary>
    [RelayCommand]
    public void HideAll() => _windowManager.HideAllNotes();
}
