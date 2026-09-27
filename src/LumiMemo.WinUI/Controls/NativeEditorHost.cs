using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LumiMemo.WinUI.Controls;

/// <summary>原生 WinUI 文本框实验宿主，仅用于验证 Acrylic 与编辑输入能否共存。</summary>
public sealed class NativeEditorHost : IDisposable
{
    private readonly Grid _host;
    private readonly TextBox _editor;
    private string _markdown = string.Empty;
    private bool _updating;

    public NativeEditorHost(Grid host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 43, 57)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 16, 18, 16)
        };
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255));
        _editor.Resources["TextControlBackground"] = transparent;
        _editor.Resources["TextControlBackgroundPointerOver"] = transparent;
        _editor.Resources["TextControlBackgroundFocused"] = transparent;
        _editor.Resources["TextControlBorderBrush"] = transparent;
        _editor.Resources["TextControlBorderBrushPointerOver"] = transparent;
        _editor.Resources["TextControlBorderBrushFocused"] = transparent;
        _editor.TextChanged += OnTextChanged;
        _host.Children.Add(_editor);
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
            _updating = true;
            try
            {
                _editor.Text = value;
            }
            finally
            {
                _updating = false;
            }
        }
    }

    public void ExecuteCommand(string command)
    {
        // 此实验只检验原生编辑控件的输入和窗口材质。格式工具稍后迁移。
    }

    public void UpdateBounds()
    {
        // 原生 WinUI 控件由 XAML 布局管理尺寸与 DPI。
    }

    private void OnTextChanged(object sender, TextChangedEventArgs args)
    {
        if (_updating || string.Equals(_markdown, _editor.Text, StringComparison.Ordinal))
        {
            return;
        }

        _markdown = _editor.Text;
        MarkdownChanged?.Invoke(this, _markdown);
    }

    public void Dispose()
    {
        _editor.TextChanged -= OnTextChanged;
        _host.Children.Remove(_editor);
    }
}
