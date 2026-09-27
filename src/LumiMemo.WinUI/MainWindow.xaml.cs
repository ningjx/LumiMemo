using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.Controls;
using Windows.Graphics;
using WinRT;

namespace LumiMemo.WinUI;

/// <summary>WinUI 3 sticky-note shell used while migrating the existing WPF application.</summary>
public sealed partial class MainWindow : Window
{
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly AppWindow _appWindow;
    private readonly RichEditorHost _editor;
    private readonly Note _note;
    private readonly INoteRepository _repository;
    private readonly IClock _clock;
    private readonly AppSettings _settings;
    private readonly ITitleGenerator _titleGenerator;
    private readonly ILayoutStore _layoutStore;
    private readonly NoteLayout _layout;
    private readonly Action<Guid> _onClosed;
    private readonly Action _onNoteChanged;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _titleTimer;
    private CancellationTokenSource? _titleRequest;
    private string _titleBaseline;
    private bool _closed;
    private bool _hasPendingSave;
    private bool _saving;
    private int _documentRevision;
    private bool _isApplicationExiting;

    public MainWindow(
        Note note,
        INoteRepository repository,
        IClock clock,
        AppSettings settings,
        ITitleGenerator titleGenerator,
        ILayoutStore layoutStore,
        NoteLayout layout,
        Action<Guid> onClosed,
        Action onNoteChanged)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(titleGenerator);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(onClosed);
        ArgumentNullException.ThrowIfNull(onNoteChanged);

        InitializeComponent();
        _appWindow = AppWindow;

        _note = note;
        _repository = repository;
        _clock = clock;
        _settings = settings;
        _titleGenerator = titleGenerator;
        _titleBaseline = note.Content;
        _layoutStore = layoutStore;
        _layout = layout;
        _onClosed = onClosed;
        _onNoteChanged = onNoteChanged;
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(settings.AutoSaveDelayMs)
        };
        _saveTimer.Tick += OnSaveTimerTick;
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _titleTimer.Tick += OnTitleTimerTick;

        _editor = new RichEditorHost(EditorHost);

        Title = "LumiMemo";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        ConfigureWindow();
        EnablePersistentAcrylic();

        TitleText.Text = note.Title;
        EditorHost.Loaded += OnEditorHostLoaded;
        _appWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        UpdateStatus(note.Content);
    }

    private async void OnEditorHostLoaded(object sender, RoutedEventArgs args)
    {
        EditorHost.Loaded -= OnEditorHostLoaded;
        try
        {
            await _editor.LoadAsync(_note.RichTextContent);
            _editor.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取便笺失败：{exception.Message}";
        }
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

    private void OnDocumentChanged(object? sender, EventArgs args)
    {
        _note.Content = _editor.PlainText;
        _note.UpdatedAt = _clock.Now;
        _titleRequest?.Cancel();
        _titleTimer.Stop();
        if (_settings.Llm.Enabled
            && !string.IsNullOrWhiteSpace(_settings.Llm.Model)
            && TitleChangePolicy.ShouldGenerate(_titleBaseline, _note.Content,
                !string.IsNullOrWhiteSpace(_note.AutoTitle)))
        {
            _titleTimer.Start();
        }
        TitleText.Text = _note.Title;
        _onNoteChanged();
        _hasPendingSave = true;
        _documentRevision++;
        StatusText.Text = $"正在保存 · {CountCharacters(_note.Content)} 字";
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
        if (!_hasPendingSave || _saving)
        {
            return;
        }

        _saving = true;
        int revision = _documentRevision;
        try
        {
            _note.RichTextContent = _editor.SaveRtf();
            await _repository.SaveAsync(_note);
            if (revision == _documentRevision)
            {
                _hasPendingSave = false;
                UpdateStatus(_note.Content);
            }
        }
        catch (Exception)
        {
            StatusText.Text = $"保存失败 · {CountCharacters(_note.Content)} 字";
        }
        finally
        {
            _saving = false;
            if (_hasPendingSave && revision != _documentRevision)
            {
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        }
    }

    private void UpdateStatus(string text) =>
        StatusText.Text = $"已保存 · {CountCharacters(text)} 字";

    private static int CountCharacters(string text) => text.EnumerateRunes().Count();

    private void OnBoldClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("bold");
    private void OnItalicClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("italic");
    private void OnUnderlineClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("underline");
    private void OnStrikeClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("strikethrough");
    private async void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await _editor.InsertImageAsync(file.Path);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"插入图片失败：{exception.Message}";
        }
    }

    private async void OnTitleTimerTick(object? sender, object e)
    {
        _titleTimer.Stop();
        string snapshot = _note.Content;
        LlmSettings config = _settings.Llm;
        if (_closed || !config.Enabled
            || !TitleChangePolicy.ShouldGenerate(_titleBaseline, snapshot,
                !string.IsNullOrWhiteSpace(_note.AutoTitle)))
        {
            return;
        }

        _titleRequest?.Dispose();
        _titleRequest = new CancellationTokenSource();
        CancellationToken token = _titleRequest.Token;
        try
        {
            string title = await _titleGenerator.GenerateAsync(snapshot, config, token);
            if (_closed || token.IsCancellationRequested || !_settings.Llm.Enabled
                || _note.Content != snapshot)
            {
                return;
            }

            _note.AutoTitle = title;
            _titleBaseline = snapshot;
            _note.UpdatedAt = _clock.Now;
            TitleText.Text = _note.Title;
            _onNoteChanged();
            _hasPendingSave = true;
            _documentRevision++;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"自动标题生成失败：{exception}");
            if (!_closed && !token.IsCancellationRequested)
            {
                StatusText.Text = "自动标题失败 · 请检查模型设置";
            }
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = PinButton.IsChecked is true;
            _layout.IsTopMost = presenter.IsAlwaysOnTop;
            _layoutStore.MarkDirty();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (SavePendingOnClose())
        {
            CaptureLayout();
            Close();
        }
    }

    public void ShowFromTray()
    {
        _layout.IsOpen = true;
        _layoutStore.MarkDirty();
        AppWindow.Show();
        Activate();
    }

    public async Task ShowAboutAsync()
    {
        ShowFromTray();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "关于 LumiMemo",
            Content = "鹿米便笺 WinUI 富文本实验版\n便笺保存在本地 .lumi 文件中。",
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    public void CloseForExit()
    {
        _isApplicationExiting = true;
        if (SavePendingOnClose())
        {
            CaptureLayout();
            Close();
        }
    }

    public void HideWindow() => AppWindow.Hide();

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!SavePendingOnClose())
        {
            args.Cancel = true;
            return;
        }

        CaptureLayout();
    }

    private bool SavePendingOnClose()
    {
        _saveTimer.Stop();
        if (!_hasPendingSave)
        {
            return true;
        }

        try
        {
            _note.RichTextContent = _editor.SaveRtf();
            _repository.SaveAsync(_note).GetAwaiter().GetResult();
            _hasPendingSave = false;
            return true;
        }
        catch (Exception)
        {
            StatusText.Text = $"保存失败 · {CountCharacters(_note.Content)} 字";
            return false;
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _titleTimer.Stop();
        _titleTimer.Tick -= OnTitleTimerTick;
        _titleRequest?.Cancel();
        _titleRequest?.Dispose();
        Closed -= OnWindowClosed;
        _onClosed(_note.Id);
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Closing -= OnWindowClosing;
        _saveTimer.Stop();
        if (!_isApplicationExiting)
        {
            _layout.IsOpen = false;
        }
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
        EditorHost.Loaded -= OnEditorHostLoaded;
        _editor.DocumentChanged -= OnDocumentChanged;
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
