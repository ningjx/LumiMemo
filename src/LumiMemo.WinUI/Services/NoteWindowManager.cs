using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;

namespace LumiMemo.WinUI.Services;

/// <summary>一张便签一个窗口的生命周期协调：映射、恢复、批量收起与退出。</summary>
public sealed class NoteWindowManager
{
    private readonly INoteStorage _storage;
    private readonly ILayoutStore _layouts;
    private readonly NoteTitleCoordinator _titles;
    private readonly NoteWindowFactory _factory;
    private readonly List<Note> _notes;
    private readonly Dictionary<Guid, MainWindow> _windows = [];

    public NoteWindowManager(
        IReadOnlyList<Note> notes,
        INoteStorage storage,
        ILayoutStore layouts,
        NoteTitleCoordinator titles,
        NoteWindowFactory windowFactory)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(windowFactory);

        _notes = [.. notes];
        _storage = storage;
        _layouts = layouts;
        _titles = titles;
        _factory = windowFactory;
        _titles.TitleUpdated += OnTitleUpdated;
        _titles.GenerationStateChanged += OnTitleGenerationStateChanged;
    }

    public event EventHandler? NotesChanged;

    public event EventHandler? TitleGenerationStateChanged;

    public IReadOnlyList<Note> Notes => _notes;

    public int OpenWindowCount => _windows.Count;

    public bool IsTitleGenerating(Guid noteId) => _titles.IsPending(noteId);

    public int RestoreOpenNotes()
    {
        int restored = 0;
        foreach (Note note in _notes.OrderBy(static item => item.CreatedAt))
        {
            if (_layouts.TryGet(note.Id)?.IsOpen is true)
            {
                OpenNote(note.Id, activate: restored == 0);
                restored++;
            }
        }

        return restored;
    }

    public void OpenNote(Guid noteId, bool activate = true)
    {
        if (_windows.TryGetValue(noteId, out MainWindow? existing))
        {
            existing.ShowFromTray();
            return;
        }

        Note? note = _notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null)
        {
            return;
        }

        NoteLayout layout = _layouts.GetOrCreate(note.Id);
        layout.IsOpen = true;
        _layouts.MarkDirty();

        MainWindow window = _factory.Create(note, layout, OnWindowClosed, OnNoteChanged);
        _windows.Add(note.Id, window);

        if (activate)
        {
            window.Activate();
        }
        else
        {
            window.AppWindow.Show();
        }
    }

    public async Task CreateNoteAsync()
    {
        Note note = await _storage.CreateAsync();
        _notes.Add(note);
        _titles.Register(note);
        NotesChanged?.Invoke(this, EventArgs.Empty);
        OpenNote(note.Id);
    }

    public void HideAllNotes()
    {
        foreach (MainWindow window in _windows.Values)
        {
            window.HideWindow();
        }
    }

    /// <summary>托盘退出路径：逐窗保存并关闭（保存失败只留日志，退出总会发生），再收掉标题协调器。</summary>
    public async Task CloseAllForExitAsync()
    {
        foreach (MainWindow window in _windows.Values.ToArray())
        {
            await window.CloseForExitAsync();
        }

        _titles.TitleUpdated -= OnTitleUpdated;
        _titles.GenerationStateChanged -= OnTitleGenerationStateChanged;
        _titles.Dispose();
    }

    private void OnWindowClosed(Guid noteId) => _windows.Remove(noteId);

    private void OnNoteChanged() => NotesChanged?.Invoke(this, EventArgs.Empty);

    private void OnTitleUpdated(Guid noteId) => NotesChanged?.Invoke(this, EventArgs.Empty);

    private void OnTitleGenerationStateChanged(Guid noteId, bool generating) =>
        TitleGenerationStateChanged?.Invoke(this, EventArgs.Empty);
}
