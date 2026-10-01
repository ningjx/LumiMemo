using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    /// <summary>细条（滚动指示）与粗条（悬停可拖）的宽度；粗条刻意不宽。</summary>
    private const double ThinWidth = 4;

    private const double ThickWidth = 8;

    /// <summary>"收起"位：滑块整体滑到右缘之外，出现/消失就是滑出/滑回。</summary>
    private const double RetractedShift = 12;

    /// <summary>滚动停止后滑块收起的等待时长。</summary>
    private static readonly TimeSpan IndicatorHideDelay = TimeSpan.FromMilliseconds(800);

    private readonly Grid _host;
    private readonly RichEditBox _editor;
    private InMemoryRandomAccessStream? _loadedStream;
    private bool _loading;
    private bool _composing;
    private string _lastEditorText = string.Empty;
    private ScrollViewer? _editorScrollViewer;
    private ScrollBar? _verticalBar;
    private Thumb? _verticalThumb;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _indicatorHideTimer;
    private bool _isPointerOverBar;

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

        // 滚动条换成"闲置隐藏、悬停加宽"的样式（EditorScrollBar.xaml，作用域仅本编辑器）。
        _editor.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///EditorScrollBar.xaml"),
        });

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
        WireScrollIndicator();
    }

    /// <summary>滚动条悬停时不该是文本的 I 形光标——它从 RichEditBox 继承了那个；给滚动条自己设成普通箭头。</summary>
    private void ApplyScrollBarCursor()
    {
        foreach (ScrollBar bar in FindDescendants<ScrollBar>(_editor))
        {
            SetArrowCursor(bar);
        }
    }

    /// <summary>
    /// 接管滚动条的显隐（模板里刻意没有 VisualState）：滚动时细条从右缘滑出指示进度、
    /// 停下向右缘收起；悬停滚动条则加宽成粗条，离开后再收起。
    /// 系统"始终显示滚动条"设置影响不到这条路。
    /// </summary>
    private void WireScrollIndicator()
    {
        _editorScrollViewer = FindDescendants<ScrollViewer>(_editor).FirstOrDefault();
        _verticalBar = FindDescendants<ScrollBar>(_editor)
            .FirstOrDefault(static bar => bar.Orientation == Orientation.Vertical);

        if (_editorScrollViewer is null || _verticalBar is null)
        {
            return;
        }

        // 滑块不在此时解析：editor.Loaded 时滚动条自己的模板还没应用，
        // 那一刻找 Thumb 只会拿到 null，之后的动画全成空操作（症状：能交互但不可见）。
        // 改在每次用的时刻惰性解析并缓存。
        _editorScrollViewer.ViewChanged += OnEditorViewChanged;
        _verticalBar.PointerEntered += OnScrollBarPointerEntered;
        _verticalBar.PointerExited += OnScrollBarPointerExited;

        _indicatorHideTimer = _editor.DispatcherQueue.CreateTimer();
        _indicatorHideTimer.Interval = IndicatorHideDelay;
        _indicatorHideTimer.IsRepeating = false;
        _indicatorHideTimer.Tick += (_, _) => HideIndicatorIfIdle();
    }

    private Thumb? ResolveVerticalThumb()
    {
        if (_verticalThumb is { } cached)
        {
            return cached;
        }

        if (_verticalBar is not { } bar)
        {
            return null;
        }

        _verticalThumb = FindDescendants<Thumb>(bar).FirstOrDefault(
            static thumb => thumb.Name == "VerticalThumb"
                || (thumb.Tag as string) == "vthumb");

        return _verticalThumb;
    }

    /// <summary>滑块的位移变换（模板里的 VerticalThumbShift），出现/收起都靠它的 X 动画。</summary>
    private TranslateTransform? ResolveVerticalShift() =>
        ResolveVerticalThumb()?.RenderTransform as TranslateTransform;

    private void OnEditorViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 有别的方式在滚（滚轮/按键/光标跟随）：细条滑出指示进度，并推迟收起。
        if (!_isPointerOverBar)
        {
            AnimateShift(ResolveVerticalShift(), 0, 150);
        }

        RestartHideTimer();
    }

    private void OnScrollBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverBar = true;
        _indicatorHideTimer?.Stop();

        if (ResolveVerticalThumb() is not { } thumb)
        {
            return;
        }

        // 细条在时由细变粗；没在时直接滑出粗条。
        AnimateWidth(thumb, ThickWidth, 167);
        AnimateShift(ResolveVerticalShift(), 0, 120);
    }

    private void OnScrollBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverBar = false;

        if (ResolveVerticalThumb() is { } thumb)
        {
            AnimateWidth(thumb, ThinWidth, 167);
        }

        RestartHideTimer();
    }

    private void HideIndicatorIfIdle()
    {
        if (_isPointerOverBar)
        {
            return;
        }

        AnimateShift(ResolveVerticalShift(), RetractedShift, 280);
    }

    private void RestartHideTimer()
    {
        _indicatorHideTimer?.Stop();
        _indicatorHideTimer?.Start();
    }

    private static void AnimateShift(TranslateTransform? transform, double to, double milliseconds)
    {
        if (transform is null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static void AnimateWidth(FrameworkElement target, double to, double milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            // 宽度是布局属性，动画必须显式允许（官方模板同款做法）。
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
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

        if (_editorScrollViewer is { } viewer)
        {
            viewer.ViewChanged -= OnEditorViewChanged;
            _editorScrollViewer = null;
        }

        if (_verticalBar is { } bar)
        {
            bar.PointerEntered -= OnScrollBarPointerEntered;
            bar.PointerExited -= OnScrollBarPointerExited;
            _verticalBar = null;
        }

        _verticalThumb = null;
        _indicatorHideTimer?.Stop();
        _indicatorHideTimer = null;

        _host.Children.Remove(_editor);
        _loadedStream?.Dispose();
    }
}
