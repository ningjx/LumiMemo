using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Controls;
using Windows.Graphics;
using WinRT;

namespace LumiMemo.WinUI;

/// <summary>WinUI 3 sticky-note shell used while migrating the existing WPF application.</summary>
public sealed partial class MainWindow : Window
{
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly MarkdownEditorHost _editor;
    private readonly Note _note;
    private readonly INoteRepository _repository;
    private readonly IClock _clock;
    private readonly ILayoutStore _layoutStore;
    private readonly NoteLayout _layout;
    private readonly DispatcherTimer _saveTimer;
    private bool _hasPendingSave;

    public MainWindow(
        Note note,
        INoteRepository repository,
        IClock clock,
        AppSettings settings,
        ILayoutStore layoutStore,
        NoteLayout layout)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(layout);

        InitializeComponent();

        _note = note;
        _repository = repository;
        _clock = clock;
        _layoutStore = layoutStore;
        _layout = layout;
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(settings.AutoSaveDelayMs)
        };
        _saveTimer.Tick += OnSaveTimerTick;

        _editor = new MarkdownEditorHost();
        EditorHost.Children.Add(_editor);

        Title = "LumiMemo";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        ConfigureWindow();
        EnablePersistentAcrylic();

        TitleText.Text = note.Title;
        _editor.Markdown = note.Content;
        _editor.MarkdownChanged += OnMarkdownChanged;
        AppWindow.Closing += OnWindowClosing;
        UpdateStatus(_editor.Markdown);
    }

    private void ConfigureWindow()
    {
        int width = Math.Max(320, (int)Math.Round(_layout.Width));
        int height = Math.Max(300, (int)Math.Round(_layout.Height));
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        bool hasSavedPosition = _layout.X != 0 || _layout.Y != 0;
        AppWindow.Move(hasSavedPosition
            ? new PointInt32((int)Math.Round(_layout.X), (int)Math.Round(_layout.Y))
            : new PointInt32(workArea.X + workArea.Width - width - 40, workArea.Y + 80));
        AppWindow.IsShownInSwitchers = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = _layout.IsTopMost;
            PinButton.IsChecked = _layout.IsTopMost;
        }

        AppWindow.Changed += OnAppWindowChanged;
    }

    private void EnablePersistentAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 247, 239, 248));
            return;
        }

        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Light
        };
        _acrylicController = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            TintColor = Windows.UI.Color.FromArgb(255, 244, 236, 255),
            TintOpacity = 0.34f,
            LuminosityOpacity = 0.58f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 247, 239, 248)
        };
        _acrylicController.AddSystemBackdropTarget(
            this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private void OnMarkdownChanged(object? sender, string markdown)
    {
        if (string.Equals(_note.Content, markdown, StringComparison.Ordinal))
        {
            return;
        }

        _note.Content = markdown;
        _note.UpdatedAt = _clock.Now;
        TitleText.Text = _note.Title;
        _hasPendingSave = true;
        StatusText.Text = $"正在保存 · {CountCharacters(markdown)} 字";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(object? sender, object e)
    {
        _saveTimer.Stop();
        await SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        if (!_hasPendingSave)
        {
            return;
        }

        try
        {
            await _repository.SaveAsync(_note);
            _hasPendingSave = false;
            UpdateStatus(_note.Content);
        }
        catch (Exception)
        {
            StatusText.Text = $"保存失败 · {CountCharacters(_note.Content)} 字";
        }
    }

    private void UpdateStatus(string markdown) =>
        StatusText.Text = $"已保存 · {CountCharacters(markdown)} 字";

    private static int CountCharacters(string markdown) => markdown.EnumerateRunes().Count();

    private void OnBoldClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("bold");
    private void OnItalicClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("italic");
    private void OnUnderlineClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("underline");
    private void OnStrikeClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("strikethrough");
    private void OnTaskListClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("taskList");
    private void OnBulletListClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("bulletList");

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = PinButton.IsChecked is true;
            _layout.IsTopMost = presenter.IsAlwaysOnTop;
            _layoutStore.MarkDirty();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _saveTimer.Stop();
        if (_hasPendingSave)
        {
            try
            {
                _repository.SaveAsync(_note).GetAwaiter().GetResult();
                _hasPendingSave = false;
            }
            catch (Exception)
            {
                args.Cancel = true;
                StatusText.Text = $"保存失败 · {CountCharacters(_note.Content)} 字";
                return;
            }
        }

        AppWindow.Changed -= OnAppWindowChanged;
        CaptureLayout();
        _layout.IsOpen = false;
        _layoutStore.MarkDirty();
        try
        {
            _layoutStore.FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Closing must remain available even if the device-state file cannot be updated.
        }

        _saveTimer.Tick -= OnSaveTimerTick;
        _editor.MarkdownChanged -= OnMarkdownChanged;
        _editor.Dispose();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        CaptureLayout();
        _layoutStore.MarkDirty();
    }

    private void CaptureLayout()
    {
        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        _layout.X = position.X;
        _layout.Y = position.Y;
        _layout.Width = size.Width;
        _layout.Height = size.Height;
        _layout.ExpandedHeight = size.Height;
    }
}
