using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LumiMemo.App.Controls;

/// <summary>在 WPF 内承载 Milkdown，并把编辑结果作为 Markdown 双向绑定。</summary>
public sealed class MarkdownEditorHost : Grid, IDisposable
{
    private const string EditorOrigin = "https://lumimemo-editor.local";
    private const int ProtocolVersion = 1;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(
        CreateEnvironmentAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownEditorHost),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnMarkdownChanged));

    public static readonly DependencyProperty IsComposingProperty = DependencyProperty.Register(
        nameof(IsComposing),
        typeof(bool),
        typeof(MarkdownEditorHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly RoutedUICommand ToggleBoldCommand = CreateCommand("粗体", "ToggleBold", Key.B, ModifierKeys.Control);
    public static readonly RoutedUICommand ToggleItalicCommand = CreateCommand("斜体", "ToggleItalic", Key.I, ModifierKeys.Control);
    public static readonly RoutedUICommand ToggleUnderlineCommand = CreateCommand("下划线", "ToggleUnderline", Key.U, ModifierKeys.Control);
    public static readonly RoutedUICommand ToggleStrikethroughCommand = CreateCommand("删除线", "ToggleStrikethrough", Key.X, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ToggleTaskListCommand = new("待办列表", "ToggleTaskList", typeof(MarkdownEditorHost));
    public static readonly RoutedUICommand ToggleBulletListCommand = new("项目列表", "ToggleBulletList", typeof(MarkdownEditorHost));

    private readonly WebView2CompositionControl _browser;
    private bool _isReady;
    private bool _isUpdatingFromEditor;
    private bool _isDisposed;
    private int _revision;

    public MarkdownEditorHost()
    {
        Background = Brushes.Transparent;

        _browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Children.Add(_browser);

        CommandBindings.Add(new CommandBinding(ToggleBoldCommand, (_, _) => Execute("bold"), CanEdit));
        CommandBindings.Add(new CommandBinding(ToggleItalicCommand, (_, _) => Execute("italic"), CanEdit));
        CommandBindings.Add(new CommandBinding(ToggleUnderlineCommand, (_, _) => Execute("underline"), CanEdit));
        CommandBindings.Add(new CommandBinding(ToggleStrikethroughCommand, (_, _) => Execute("strikethrough"), CanEdit));
        CommandBindings.Add(new CommandBinding(ToggleTaskListCommand, (_, _) => Execute("taskList"), CanEdit));
        CommandBindings.Add(new CommandBinding(ToggleBulletListCommand, (_, _) => Execute("bulletList"), CanEdit));

        Loaded += OnLoaded;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsComposing
    {
        get => (bool)GetValue(IsComposingProperty);
        set => SetValue(IsComposingProperty, value);
    }

    private static RoutedUICommand CreateCommand(string text, string name, Key key, ModifierKeys modifiers) =>
        new(text, name, typeof(MarkdownEditorHost), [new KeyGesture(key, modifiers)]);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            CoreWebView2Environment environment = await SharedEnvironment.Value;
            await _browser.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _browser.CoreWebView2;
            ConfigureSecurity(core);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, args) => args.Handled = true;

            string assets = Path.Combine(AppContext.BaseDirectory, "EditorAssets");
            core.SetVirtualHostNameToFolderMapping(
                "lumimemo-editor.local",
                assets,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _browser.Source = new Uri($"{EditorOrigin}/index.html");
        }
        catch (Exception exception)
        {
            ShowInitializationError(exception);
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumiMemo",
            "WebView2");
        Directory.CreateDirectory(userData);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
    }

    private static void ConfigureSecurity(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith(EditorOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!string.Equals(args.Source, EditorOrigin + "/", StringComparison.OrdinalIgnoreCase)
            && !args.Source.StartsWith(EditorOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("version", out JsonElement version)
                || version.GetInt32() != ProtocolVersion
                || !root.TryGetProperty("type", out JsonElement type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "ready":
                    _isReady = true;
                    SendDocument();
                    SendTheme();
                    CommandManager.InvalidateRequerySuggested();
                    break;
                case "contentChanged" when root.TryGetProperty("markdown", out JsonElement content):
                    string markdown = content.GetString() ?? string.Empty;
                    if (markdown == Markdown)
                    {
                        return;
                    }

                    _isUpdatingFromEditor = true;
                    SetCurrentValue(MarkdownProperty, markdown);
                    _isUpdatingFromEditor = false;
                    break;
                case "compositionChanged" when root.TryGetProperty("isComposing", out JsonElement composing):
                    SetCurrentValue(IsComposingProperty, composing.GetBoolean());
                    break;
            }
        }
        catch (JsonException)
        {
            // 来自隔离编辑器的畸形消息不应影响便签进程。
        }
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var editor = (MarkdownEditorHost)sender;
        if (!editor._isUpdatingFromEditor)
        {
            editor.SendDocument();
        }
    }

    private void SendDocument()
    {
        if (!_isReady || _browser.CoreWebView2 is null)
        {
            return;
        }

        _revision++;
        Post(new { version = ProtocolVersion, type = "loadDocument", markdown = Markdown ?? string.Empty, revision = _revision });
    }

    private void SendTheme()
    {
        Post(new
        {
            version = ProtocolVersion,
            type = "setTheme",
            theme = new
            {
                foreground = BrushToCss("LumiNoteTextBrush", "#3f3745"),
                muted = BrushToCss("LumiNoteActionBrush", "#75697c"),
                accent = BrushToCss("LumiAccentBrush", "#8d6aa3"),
                selection = "rgba(178,132,210,.28)",
            },
        });
    }

    private string BrushToCss(string key, string fallback)
    {
        if (TryFindResource(key) is not SolidColorBrush brush)
        {
            return fallback;
        }

        Color color = brush.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void Execute(string command)
    {
        Post(new { version = ProtocolVersion, type = "executeCommand", command });
    }

    private void CanEdit(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _isReady;

    private void Post(object message)
    {
        if (_isReady && _browser.CoreWebView2 is not null)
        {
            _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    private void ShowInitializationError(Exception exception)
    {
        Children.Clear();
        Children.Add(new TextBlock
        {
            Text = $"编辑器无法启动：{exception.Message}",
            Margin = new Thickness(14, 12, 14, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Firebrick,
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_browser.CoreWebView2 is not null)
        {
            _browser.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
        }
        _browser.Dispose();
    }
}
