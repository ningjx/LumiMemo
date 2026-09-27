using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>Hosts the existing Milkdown editor in a WinUI WebView2 control.</summary>
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
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private readonly WebView2 _browser;
    private bool _isReady;
    private bool _isUpdatingFromEditor;
    private bool _isDisposed;
    private int _revision;

    public MarkdownEditorHost()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _browser = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        };
        Children.Add(_browser);
        Loaded += OnLoaded;
    }

    public event EventHandler<string>? MarkdownChanged;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsReady => _isReady;

    public void ExecuteCommand(string command) =>
        Post(new { version = ProtocolVersion, type = "executeCommand", command });

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
            core.NewWindowRequested += OnNewWindowRequested;

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
        return await CoreWebView2Environment.CreateAsync();
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

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!args.Source.StartsWith(EditorOrigin + "/", StringComparison.OrdinalIgnoreCase))
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
                    break;
                case "contentChanged" when root.TryGetProperty("markdown", out JsonElement content):
                    string markdown = content.GetString() ?? string.Empty;
                    if (markdown == Markdown)
                    {
                        return;
                    }

                    _isUpdatingFromEditor = true;
                    Markdown = markdown;
                    _isUpdatingFromEditor = false;
                    MarkdownChanged?.Invoke(this, markdown);
                    break;
            }
        }
        catch (JsonException)
        {
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
        Post(new
        {
            version = ProtocolVersion,
            type = "loadDocument",
            markdown = Markdown ?? string.Empty,
            revision = _revision
        });
    }

    private void SendTheme() => Post(new
    {
        version = ProtocolVersion,
        type = "setTheme",
        theme = new
        {
            foreground = "#3f3745",
            muted = "#75697c",
            accent = "#8d6aa3",
            selection = "rgba(178,132,210,.28)"
        }
    });

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
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 34, 34))
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
            _browser.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
        }
        _browser.Close();
    }
}
