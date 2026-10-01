using CommunityToolkit.Mvvm.ComponentModel;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Search;
using LumiMemo.WinUI.Services;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>便笺列表窗口的状态：查询、过滤结果与开窗动作。</summary>
/// <remarks>
/// 原实现把过滤写在窗口的 code-behind 里（内联 <c>Contains</c>），绕过了 Core 的
/// <see cref="NoteSearch"/>——评分、置顶加权、确定性排序全都没有。这里回到那条正路。
/// </remarks>
public sealed class ManagerViewModel : ObservableObject, IDisposable
{
    private readonly ILayoutStore _layouts;
    private readonly IClock _clock;
    private readonly NoteWindowManager _windows;

    private string _query = string.Empty;
    private IReadOnlyList<NoteListItem> _items = [];
    private bool _isDisposed;

    public ManagerViewModel(
        ILayoutStore layouts,
        IClock clock,
        NoteWindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(windows);

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

    /// <summary>当前列表（已按查询过滤排序）；整表替换 + 变更通知。</summary>
    public IReadOnlyList<NoteListItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

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

    /// <summary>重建列表：按当前查询走排序或搜索。便签变化事件进来时自动调用，也可显式调。</summary>
    /// <remarks>
    /// 便签集合每次都从 <see cref="NoteWindowManager"/> 现取——它的列表才是唯一可变的真身
    /// （新建、删除、恢复都改它）。自己持一份构造时的快照会与管理器的增删脱钩：
    /// 删除后列表不更新、新建后不出现，都是那个形态的后果。
    /// </remarks>
    public void Refresh()
    {
        IReadOnlyList<Note> notes = _windows.Notes;
        string query = Query.Trim();

        IReadOnlyList<Note> ordered;
        if (query.Length == 0)
        {
            ordered = [.. NoteSearch.OrderForList(notes)];
        }
        else
        {
            // 每次刷新重建「id → 纯文本」映射：便签集合是可变的（新建、标题更新都会改它）。
            Dictionary<Guid, string> plainText =
                notes.ToDictionary(static note => note.Id, static note => note.Content);

            ordered =
            [
                .. NoteSearch.Search(notes, query, id => plainText[id], TopMostIds(notes), _clock.Now)
                    .Select(static hit => hit.Note),
            ];
        }

        Items = [.. ordered.Select(note => new NoteListItem(note, _windows.IsTitleGenerating(note.Id)))];
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
