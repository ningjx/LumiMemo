using Microsoft.UI.Xaml;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.Infrastructure.Windows;
using LumiMemo.WinUI.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LumiMemo.WinUI;

/// <summary>WinUI application entry point for the UI migration.</summary>
public partial class App : Application
{
    private TrayIconService? _trayIcon;
    private NoteWindowManager? _windowManager;
    private ManagerWindow? _managerWindow;
    private HttpClient? _titleHttpClient;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            (IReadOnlyList<Note> notes, INoteRepository repository, IClock clock,
                AppSettings settings, ILayoutStore layoutStore, ISettingsStore settingsStore) =
                await LoadStartupDataAsync();

            _titleHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var titleGenerator = new OpenAiCompatibleTitleGenerator(_titleHttpClient);
            _windowManager = new NoteWindowManager(notes, repository, clock, settings, layoutStore, titleGenerator);
            _managerWindow = new ManagerWindow(_windowManager, settings, settingsStore);

            if (settings.ShowTrayIcon)
            {
                _trayIcon = new TrayIconService();
                _trayIcon.Start(
                    () => _ = _windowManager.CreateNoteAsync(),
                    ShowAllNotes,
                    _windowManager.HideAllNotes,
                    _managerWindow.ShowWindow,
                    () => OpenNotesFolder(settings.NotesFolder),
                    () => _ = _managerWindow.ShowAboutAsync(),
                    ExitFromTray);
            }

            int restored = _windowManager.RestoreOpenNotes();
            if (restored == 0)
            {
                _managerWindow.ShowWindow();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            Exit();
        }
    }

    private void ShowAllNotes()
    {
        if (_windowManager is null || _managerWindow is null)
        {
            return;
        }

        _windowManager.ShowAllNotes();
        if (_windowManager.OpenWindowCount == 0)
        {
            _managerWindow.ShowWindow();
        }
    }

    private static void OpenNotesFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void ExitFromTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        _managerWindow?.CloseForExit();
        _managerWindow = null;
        _windowManager?.CloseAllForExit();
        _windowManager = null;

        _titleHttpClient?.Dispose();
        _titleHttpClient = null;

        Exit();
    }

    private static async Task<(
        IReadOnlyList<Note> Notes,
        INoteRepository Repository,
        IClock Clock,
        AppSettings Settings,
        ILayoutStore LayoutStore,
        ISettingsStore SettingsStore)>
        LoadStartupDataAsync()
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

        var repository = new LumiNoteRepository(
            settings.NotesFolder, clock, writer, settings.DefaultColor);

        IReadOnlyList<Note> notes = await repository.LoadAllAsync();

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

        return (notes, repository, clock, settings, layoutStore, settingsStore);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}
