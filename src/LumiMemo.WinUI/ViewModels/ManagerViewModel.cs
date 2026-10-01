using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using LumiMemo.WinUI.Services;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>便笺列表窗口的状态：查询、筛选、排序与开窗动作。</summary>
/// <remarks>
/// <para>
/// 列表构建统一走 <see cref="INoteSearchProvider"/>：本地关键词搜索是当前唯一实现，
/// 计划中的 AI 搜索将实现同一接口——届时组合根换注入即可，本类不用改。
/// </para>
/// <para>
/// 便签集合每次都从 <see cref="NoteWindowManager"/> 现取——它的列表才是唯一可变的真身
/// （新建、删除、恢复都改它）。自己持一份构造时的快照会与管理器的增删脱钩：
/// 删除后列表不更新、新建后不出现，都是那个形态的后果。
/// </para>
/// </remarks>
public sealed class ManagerViewModel : ObservableObject, IDisposable
{
    private readonly INoteSearchProvider _search;
    private readonly ILayoutStore _layouts;
    private readonly IClock _clock;
    private readonly NoteWindowManager _windows;

    private string _query = string.Empty;
    private NoteColor? _colorFilter;
    private bool _sortByModifiedTime;
    private IReadOnlyList<NoteListItem> _items = [];
    private int _totalCount;
    private int _shownCount;
    private int _refreshVersion;
    private bool _isDisposed;

    public ManagerViewModel(
        INoteSearchProvider search,
        ILayoutStore layouts,
        IClock clock,
        NoteWindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(windows);

        _search = search;
        _layouts = layouts;
        _clock = clock;
        _windows = windows;

        _windows.NotesChanged += OnNotesChanged;
        _windows.TitleGenerationStateChanged += OnNotesChanged;

        Refresh();
    }

    /// <summary>搜索词。由搜索框的 TextChanged 转发进来，输入即刷新。</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>颜色筛选；<see langword="null"/> 表示不筛。</summary>
    public NoteColor? ColorFilter
    {
        get => _colorFilter;
        set
        {
            if (SetProperty(ref _colorFilter, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>排序是否改为「按修改时间」；false = 默认顺序（有查询词按相关度）。</summary>
    /// <remarks>
    /// 排序链的多键框架在 Core（<c>NoteSearch.SortHits</c> + <c>NoteSearchRequest.Sorts</c>）；
    /// 本轮的界面只提供一个条件，VM 先用布尔形态表达，后续补条件时再升级为链。
    /// </remarks>
    public bool SortByModifiedTime
    {
        get => _sortByModifiedTime;
        set
        {
            if (SetProperty(ref _sortByModifiedTime, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>当前列表（已按查询、筛选、排序处理）；整表替换 + 变更通知。</summary>
    public IReadOnlyList<NoteListItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>便签总数（不受查询/筛选影响）。</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>当前展示的条数（搜索/筛选后）。</summary>
    public int ShownCount
    {
        get => _shownCount;
        private set
        {
            if (SetProperty(ref _shownCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>右下角计数：没有搜索/筛选时就是总数（"10"），否则是"2/10"。</summary>
    public string CountText => HasActiveFilter
        ? $"{_shownCount}/{_totalCount}"
        : _totalCount.ToString(CultureInfo.InvariantCulture);

    private bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(_query) || _colorFilter is not null;

    public void OpenNote(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        _windows.OpenNote(note.Id);
    }

    public Task CreateNoteAsync() => _windows.CreateNoteAsync();

    /// <summary>删除一张便签（先落盘再移入回收站）；窗口保存失败时返回 false 且不删除。</summary>
    public Task<bool> DeleteNoteAsync(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return _windows.DeleteNoteAsync(note);
    }

    /// <summary>改一张便签的颜色并落盘；失败时向上抛（调用方弹提示）。</summary>
    public Task ChangeColorAsync(Note note, NoteColor color)
    {
        ArgumentNullException.ThrowIfNull(note);

        return _windows.ChangeColorAsync(note, color);
    }

    /// <summary>复制一张便签（原样复制），副本出现在列表里。</summary>
    public Task<Note> DuplicateNoteAsync(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return _windows.DuplicateNoteAsync(note);
    }

    /// <summary>重建列表。便签变化事件进来时自动调用，也可显式调。</summary>
    public void Refresh() => _ = RefreshAsync();

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _windows.NotesChanged -= OnNotesChanged;
        _windows.TitleGenerationStateChanged -= OnNotesChanged;
    }

    private void OnNotesChanged(object? sender, EventArgs e) => Refresh();

    private async Task RefreshAsync()
    {
        IReadOnlyList<Note> notes = _windows.Notes;

        var request = new NoteSearchRequest
        {
            Notes = notes,
            Query = _query,
            Sorts = _sortByModifiedTime ? [NoteSortOrder.ModifiedTime] : [],
            Color = _colorFilter,
            TopMostIds = TopMostIds(notes),
            Now = _clock.Now,
        };

        // 过期的结果不许覆盖新结果（本地搜索同步完成、不会乱序；这是为将来
        // 会走网络的 AI 搜索实现预留的守卫——输入比响应快是那种实现的常态）。
        int version = ++_refreshVersion;

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await _search.SearchAsync(request);
        }
        catch (Exception exception)
        {
            // 本地实现不抛；兜底是为了 fire-and-forget（Build 在 Refresh 里）的异常
            // 不至于变成没人管的未观察任务——列表保持上一份内容。
            System.Diagnostics.Debug.WriteLine(exception);
            return;
        }

        if (version != _refreshVersion || _isDisposed)
        {
            return;
        }

        // 摘要按当前查询词在标题与正文里分别定位；没有查询词时退化为开头预览。
        string[] terms = NoteSearch.SplitTerms(_query);

        Items =
        [
            .. hits.Select(hit => new NoteListItem(
                hit.Note,
                _windows.IsTitleGenerating(hit.Note.Id),
                SnippetBuilder.Build(hit.Note.Title, terms),
                SnippetBuilder.Build(hit.Note.Content, terms))),
        ];

        TotalCount = notes.Count;
        ShownCount = Items.Count;
    }

    /// <summary>置顶便签的 id 集合（§12.2 的加分项）；一张都没有时返回 null 省一次加分支。</summary>
    private IReadOnlySet<Guid>? TopMostIds(IReadOnlyList<Note> notes)
    {
        HashSet<Guid>? result = null;

        foreach (Note note in notes)
        {
            if (_layouts.TryGet(note.Id)?.IsTopMost is true)
            {
                result ??= [];
                result.Add(note.Id);
            }
        }

        return result;
    }
}
