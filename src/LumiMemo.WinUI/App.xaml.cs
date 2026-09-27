using Microsoft.UI.Xaml;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace LumiMemo.WinUI;

/// <summary>WinUI application entry point for the UI migration.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            (Note note, INoteRepository repository, IClock clock, AppSettings settings,
                ILayoutStore layoutStore, NoteLayout layout) =
                await LoadStartupNoteAsync();

            _window = new MainWindow(note, repository, clock, settings, layoutStore, layout);
            _window.Activate();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            Exit();
        }
    }

    private static async Task<(
        Note Note,
        INoteRepository Repository,
        IClock Clock,
        AppSettings Settings,
        ILayoutStore LayoutStore,
        NoteLayout Layout)>
        LoadStartupNoteAsync()
    {
        var paths = new AppPaths();
        paths.EnsureLocalAppDataDirectories();

        var clock = new SystemClock();
        var writer = new AtomicFileWriter(clock);
        var settingsStore = new JsonSettingsStore(
            paths,
            clock,
            writer,
            NullLogger<JsonSettingsStore>.Instance);
        AppSettings settings = await settingsStore.LoadAsync();

        if (string.IsNullOrWhiteSpace(settings.NotesFolder))
        {
            settings.NotesFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "LumiMemo");
            Directory.CreateDirectory(settings.NotesFolder);
            await settingsStore.SaveAsync(settings);
        }

        paths.SetNotesFolder(settings.NotesFolder);
        paths.SetAttachmentsFolderName(settings.AttachmentsFolderName);

        var repository = new MarkdownNoteRepository(
            paths,
            clock,
            writer,
            NullLogger<MarkdownNoteRepository>.Instance)
        {
            DefaultColor = settings.DefaultColor
        };

        IReadOnlyList<Note> notes = await repository.LoadAllAsync();
        Note note = notes
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault()
            ?? await repository.CreateAsync();

        var layoutStore = new JsonLayoutStore(
            paths,
            clock,
            writer,
            new MonitorEnumerator(),
            NullLogger<JsonLayoutStore>.Instance)
        {
            DefaultWidth = settings.DefaultWidth,
            DefaultHeight = settings.DefaultHeight
        };
        await layoutStore.LoadAsync();
        NoteLayout layout = layoutStore.GetOrCreate(note.Id);
        layout.IsOpen = true;
        layoutStore.MarkDirty();

        return (note, repository, clock, settings, layoutStore, layout);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}
