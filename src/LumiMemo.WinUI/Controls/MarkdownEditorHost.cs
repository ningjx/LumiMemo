using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>
/// Hosts the local Milkdown editor in WinUI's supported XAML WebView2 control.
/// The editor surface uses a theme-matched opaque fallback because WinUI 3 WebView2
/// does not currently support a genuinely transparent background.
/// </summary>
public sealed class MarkdownEditorHost : IDisposable
{
    private const string EditorOrigin = "https://lumimemo-editor.local";
    private const int ProtocolVersion = 1;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(
        CreateEnvironmentAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Grid _boundsHost;
    private readonly WebView2 _browser;
    private CoreWebView2? _core;
    private string _markdown = string.Empty;
    private bool _isReady;
    private bool _isDisposed;
    private int _revision;

    public MarkdownEditorHost(Grid boundsHost)
    {
        ArgumentNullException.ThrowIfNull(boundsHost);

        _boundsHost = boundsHost;
        _browser = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 244, 236, 250)
        };
        _boundsHost.Children.Add(_browser);
        _boundsHost.Loaded += OnLoaded;
    }

    public event EventHandler<string>? MarkdownChanged;

    public string Markdown
    {
        get => _markdown;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_markdown, value, StringComparison.Ordinal))
            {
                return;
            }

            _markdown = value;
            SendDocument();
        }
    }

    public bool IsReady => _isReady;

    public void ExecuteCommand(string command) =>
        Post(new { version = ProtocolVersion, type = "executeCommand", command });

    public void UpdateBounds()
    {
        // XAML owns the control's layout and DPI scaling.
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _boundsHost.Loaded -= OnLoaded;

        try
        {
            CoreWebView2Environment environment = await SharedEnvironment.Value;
            await _browser.EnsureCoreWebView2Async(environment);
            _core = _browser.CoreWebView2;

            ConfigureSecurity(_core);
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NewWindowRequested += OnNewWindowRequested;

            string assets = Path.Combine(AppContext.BaseDirectory, "EditorAssets");
            _core.SetVirtualHostNameToFolderMapping(
                "lumimemo-editor.local",
                assets,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _core.Navigate($"{EditorOrigin}/index.html");
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

        return await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            userData,
            new CoreWebView2EnvironmentOptions());
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
                    if (string.Equals(markdown, _markdown, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _markdown = markdown;
                    MarkdownChanged?.Invoke(this, markdown);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private void SendDocument()
    {
        if (!_isReady)
        {
            return;
        }

        _revision++;
        Post(new
        {
            version = ProtocolVersion,
            type = "loadDocument",
            markdown = _markdown,
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
        if (_isReady && _core is not null)
        {
            _core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    private void ShowInitializationError(Exception exception)
    {
        _boundsHost.Children.Clear();
        _boundsHost.Children.Add(new TextBlock
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
        _boundsHost.Loaded -= OnLoaded;

        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationStarting -= OnNavigationStarting;
            _core.NewWindowRequested -= OnNewWindowRequested;
        }

        _browser.Close();
        _boundsHost.Children.Remove(_browser);
        _core = null;
    }
}
