using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>原生富文本编辑器；RTF 作为无损正文，纯文本仅用于标题和搜索。</summary>
/// <remarks>
/// 实现 <see cref="IRichTextDocument"/> 供 ViewModel 消费；IME 组字期间不发
/// <see cref="IRichTextDocument.UserEdited"/>——半截拼音不该被当成一次编辑去触发保存（§15.6）。
/// </remarks>
public sealed class RichEditorHost : IRichTextDocument, IDisposable
{
    private readonly Grid _host;
    private readonly RichEditBox _editor;
    private InMemoryRandomAccessStream? _loadedStream;
    private bool _loading;
    private bool _composing;
    private string _lastEditorText = string.Empty;

    public RichEditorHost(Grid host)
    {
        _host = host;
        _editor = new RichEditBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // 半透的淡紫白薄纱底：只要一点点，保住毛玻璃观感的同时让反色光标偏深
            // （WinUI 光标是"光标下像素的反色"，底完全透明时光标随壁纸深浅漂移）。
            Background = EditorFrost(),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 43, 57)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(18, 16, 18, 16)
        };

        // 正文自动换行，横向滚动条没有存在价值；禁掉免得它偶尔露头。
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Disabled);

        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"
        })
        {
            _editor.Resources[key] = EditorFrost();
        }

        foreach (string key in new[]
        {
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused"
        })
        {
            _editor.Resources[key] = Transparent();
        }

        _editor.TextChanged += OnTextChanged;
        _editor.TextCompositionStarted += OnCompositionStarted;
        _editor.TextCompositionEnded += OnCompositionEnded;
        _editor.KeyDown += OnKeyDown;
        _editor.Loaded += OnEditorLoaded;
        _host.Children.Add(_editor);
    }

    /// <summary>仅在正文实际变化或用户执行格式、插图命令时触发。</summary>
    public event EventHandler? UserEdited;

    public string PlainText
    {
        get
        {
            _editor.Document.GetText(TextGetOptions.None, out string text);
            return text.TrimEnd('\r', '\n');
        }
    }

    public async Task LoadAsync(byte[] rtf)
    {
        _loading = true;
        try
        {
            if (rtf.Length == 0)
            {
                _editor.Document.SetText(TextSetOptions.None, string.Empty);
            }
            else
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(rtf);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);
                _editor.Document.LoadFromStream(TextSetOptions.FormatRtf, stream);
                _loadedStream = stream;
            }
            _lastEditorText = PlainText;
        }
        finally
        {
            _loading = false;
        }
    }

    public byte[] SaveRtf()
    {
        using var stream = new InMemoryRandomAccessStream();
        _editor.Document.SaveToStream(TextGetOptions.FormatRtf, stream);
        stream.Seek(0);
        using var memory = new MemoryStream();
        stream.AsStreamForRead().CopyTo(memory);
        return memory.ToArray();
    }

    public void ExecuteCommand(string command)
    {
        ITextCharacterFormat format = _editor.Document.Selection.CharacterFormat;
        switch (command)
        {
            case "bold":
                format.Bold = format.Bold != FormatEffect.On ? FormatEffect.On : FormatEffect.Off;
                break;
            case "italic":
                format.Italic = format.Italic != FormatEffect.On ? FormatEffect.On : FormatEffect.Off;
                break;
            case "underline":
                format.Underline = format.Underline == UnderlineType.Single
                    ? UnderlineType.None : UnderlineType.Single;
                break;
            case "strikethrough":
                format.Strikethrough = format.Strikethrough != FormatEffect.On
                    ? FormatEffect.On : FormatEffect.Off;
                break;
            default:
                return;
        }

        NotifyUserEdited();
        _editor.Focus(FocusState.Programmatic);
    }

    public async Task InsertImageAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1.0, 280.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale));
        stream.Seek(0);
        _editor.Document.Selection.InsertImage(
            width, height, 0, VerticalCharacterAlignment.Baseline, file.Name, stream);
        NotifyUserEdited();
        _editor.Focus(FocusState.Programmatic);
    }

    private static SolidColorBrush Transparent() =>
        new(Windows.UI.Color.FromArgb(0, 255, 255, 255));

    /// <summary>编辑区底色：一点点淡紫白薄纱（约两成），毛玻璃基本透出来，反色光标略偏深。</summary>
    private static SolidColorBrush EditorFrost() =>
        new(Windows.UI.Color.FromArgb(0x2E, 0xF7, 0xF3, 0xFD));

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        _editor.Loaded -= OnEditorLoaded;
        ApplyScrollBarCursor();

        // 滚动条的滑出/收起行为（全程序通用）。
        ScrollBarReveal.AttachTo(_editor);
    }

    /// <summary>滚动条悬停时不该是文本的 I 形光标——它从 RichEditBox 继承了那个；给滚动条自己设成普通箭头。</summary>
    private void ApplyScrollBarCursor()
    {
        foreach (ScrollBar bar in ScrollBarReveal.FindAll<ScrollBar>(_editor))
        {
            SetArrowCursor(bar);
        }
    }

    private static void SetArrowCursor(UIElement element)
    {
        try
        {
            // ProtectedCursor 是 protected 成员，XAML 与公开 API 都够不着；
            // 反射设置是 WinUI 3 里给模板内部元素改光标的惯用手法。
            typeof(UIElement)
                .GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(element, InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        }
        catch (Exception)
        {
            // 光标只是观感，设不上就保持默认。
        }
    }

    private void OnTextChanged(object sender, RoutedEventArgs args)
    {
        // 组字期间的变化一律压下：拼音串不是内容，等 TextCompositionEnded 再比一次。
        if (_loading || _composing || string.Equals(PlainText, _lastEditorText, StringComparison.Ordinal))
        {
            return;
        }

        NotifyUserEdited();
    }

    private void OnCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args) =>
        _composing = true;

    private void OnCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _composing = false;

        // 组字期间的 TextChanged 被压下了；结束时补一次比较，确实落了字的算用户编辑。
        if (!string.Equals(PlainText, _lastEditorText, StringComparison.Ordinal))
        {
            NotifyUserEdited();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        CoreVirtualKeyStates control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if ((control & CoreVirtualKeyStates.Down) == 0)
        {
            return;
        }

        string? command = args.Key switch
        {
            VirtualKey.B => "bold",
            VirtualKey.I => "italic",
            VirtualKey.U => "underline",
            _ => null
        };
        if (command is not null)
        {
            args.Handled = true;
            ExecuteCommand(command);
        }
    }

    private void NotifyUserEdited()
    {
        _lastEditorText = PlainText;
        UserEdited?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _editor.TextChanged -= OnTextChanged;
        _editor.TextCompositionStarted -= OnCompositionStarted;
        _editor.TextCompositionEnded -= OnCompositionEnded;
        _editor.KeyDown -= OnKeyDown;
        _editor.Loaded -= OnEditorLoaded;

        _host.Children.Remove(_editor);
        _loadedStream?.Dispose();
    }
}
