using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>
/// Hosts Milkdown through the HWND WebView2 controller. The WinUI XAML WebView2 wrapper
/// flattens transparent pixels against an opaque island; the HWND controller preserves them
/// so the window-level Acrylic remains visible through the editor.
/// </summary>
public sealed class MarkdownEditorHost : IDisposable
{
    private const string EditorOrigin = "https://lumimemo-editor.local";
    private const int ProtocolVersion = 1;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(
        CreateEnvironmentAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Grid _boundsHost;
    private readonly nint _parentWindow;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private string _markdown = string.Empty;
    private bool _isReady;
    private bool _isDisposed;
    private int _revision;

    public MarkdownEditorHost(Grid boundsHost, nint parentWindow)
    {
        ArgumentNullException.ThrowIfNull(boundsHost);
        if (parentWindow == 0)
        {
            throw new ArgumentException("WebView2 需要有效的父窗口句柄。", nameof(parentWindow));
        }

        _boundsHost = boundsHost;
        _parentWindow = parentWindow;
        _boundsHost.Loaded += OnLoaded;
        _boundsHost.SizeChanged += OnHostSizeChanged;
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
        if (_controller is null || _boundsHost.XamlRoot is null)
        {
            return;
        }

        Windows.Foundation.Point origin = _boundsHost
            .TransformToVisual(null)
            .TransformPoint(new Windows.Foundation.Point());
        double scale = _boundsHost.XamlRoot.RasterizationScale;

        _controller.Bounds = new Windows.Foundation.Rect(
            Math.Round(origin.X * scale),
            Math.Round(origin.Y * scale),
            Math.Max(1, Math.Round(_boundsHost.ActualWidth * scale)),
            Math.Max(1, Math.Round(_boundsHost.ActualHeight * scale)));
        _controller.RasterizationScale = scale;
        _controller.IsVisible = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _boundsHost.Loaded -= OnLoaded;

        try
        {
            CoreWebView2Environment environment = await SharedEnvironment.Value;
            CoreWebView2ControllerWindowReference parent =
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
                    unchecked((ulong)_parentWindow.ToInt64()));
            _controller = await environment.CreateCoreWebView2ControllerAsync(parent);
            _controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            _core = _controller.CoreWebView2;

            ConfigureSecurity(_core);
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NewWindowRequested += OnNewWindowRequested;

            string assets = Path.Combine(AppContext.BaseDirectory, "EditorAssets");
            _core.SetVirtualHostNameToFolderMapping(
                "lumimemo-editor.local",
                assets,
                CoreWebView2HostResourceAccessKind.DenyCors);

            UpdateBounds();
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

        // Core WebView2 reads this before controller creation and avoids an initial white frame.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
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

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBounds();

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
        _boundsHost.SizeChanged -= OnHostSizeChanged;

        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationStarting -= OnNavigationStarting;
            _core.NewWindowRequested -= OnNewWindowRequested;
        }

        _controller?.Close();
        _controller = null;
        _core = null;
    }
}
