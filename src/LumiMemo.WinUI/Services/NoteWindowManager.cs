using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;

namespace LumiMemo.WinUI.Services;

/// <summary>Coordinates the one-window-per-note lifecycle for the WinUI shell.</summary>
public sealed class NoteWindowManager
{
    private readonly INoteRepository _repository;
    private readonly IClock _clock;
    private readonly AppSettings _settings;
    private readonly ILayoutStore _layouts;
    private readonly NoteTitleCoordinator _titles;
    private readonly List<Note> _notes;
    private readonly Dictionary<Guid, MainWindow> _windows = [];

    public NoteWindowManager(
        IReadOnlyList<Note> notes,
        INoteRepository repository,
        IClock clock,
        AppSettings settings,
        ILayoutStore layouts,
        NoteTitleCoordinator titles)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(titles);

        _notes = [.. notes];
        _repository = repository;
        _clock = clock;
        _settings = settings;
        _layouts = layouts;
        _titles = titles;
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
        foreach (Note note in _notes.OrderBy(item => item.CreatedAt))
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

        var window = new MainWindow(
            note,
            _repository,
            _clock,
            _settings,
            _titles,
            _layouts,
            layout,
            OnWindowClosed,
            OnNoteChanged);
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
        Note note = await _repository.CreateAsync();
        _notes.Add(note);
        _titles.Register(note);
        NotesChanged?.Invoke(this, EventArgs.Empty);
        OpenNote(note.Id);
    }

    public void ShowAllNotes()
    {
        foreach (MainWindow window in _windows.Values)
        {
            window.ShowFromTray();
        }
    }

    public void HideAllNotes()
    {
        foreach (MainWindow window in _windows.Values)
        {
            window.HideWindow();
        }
    }

    public void CloseAllForExit()
    {
        foreach (MainWindow window in _windows.Values.ToArray())
        {
            window.CloseForExit();
        }

        _layouts.FlushAsync().GetAwaiter().GetResult();
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
