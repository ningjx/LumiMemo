using Microsoft.UI.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>原生富文本编辑器；RTF 作为无损正文，纯文本仅用于标题和搜索。</summary>
public sealed class RichEditorHost : IDisposable
{
    private readonly Grid _host;
    private readonly RichEditBox _editor;
    private InMemoryRandomAccessStream? _loadedStream;
    private bool _loading;
    private string _lastEditorText = string.Empty;

    public RichEditorHost(Grid host)
    {
        _host = host;
        _editor = new RichEditBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Transparent(),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 43, 57)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 16, 18, 16)
        };

        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused"
        })
        {
            _editor.Resources[key] = Transparent();
        }

        _editor.TextChanged += OnTextChanged;
        _editor.KeyDown += OnKeyDown;
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

    private void OnTextChanged(object sender, RoutedEventArgs args)
    {
        if (_loading || string.Equals(PlainText, _lastEditorText, StringComparison.Ordinal))
        {
            return;
        }

        NotifyUserEdited();
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
        _editor.KeyDown -= OnKeyDown;
        _host.Children.Remove(_editor);
        _loadedStream?.Dispose();
    }
}
