using LumiMemo.Core.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace LumiMemo.WinUI.Controls;

/// <summary>原生富文本编辑器；RTF 作为无损正文，纯文本仅用于标题和搜索。</summary>
/// <remarks>
/// 实现 <see cref="IRichTextDocument"/> 供 ViewModel 消费；IME 组字期间不发
/// <see cref="IRichTextDocument.UserEdited"/>——半截拼音不该被当成一次编辑去触发保存（§15.6）。
/// 图片缩放的手柄覆盖层见 <see cref="ImageAdorner"/>；待办/分点是段落层面的命令，
/// 由窗口工具栏转发 <see cref="ExecuteCommand"/>。
/// </remarks>
public sealed class RichEditorHost : IRichTextDocument, IDisposable
{
    /// <summary>插图时的长边上限（px）：贴进来的都先缩到这个量级，再让用户自己拉。</summary>
    private const double MaxInsertImageEdge = 280.0;

    /// <summary>按下与松开位移小于它才算「点击」（切换待办方框），否则视为拖选。</summary>
    private const double ClickSlack = 4;

    /// <summary>勾选后的行文字颜色（灰）与恢复用的默认正文色。</summary>
    private static readonly Color CheckedLineForeground = Color.FromArgb(255, 0x75, 0x69, 0x7C);

    private static readonly Color DefaultLineForeground = Color.FromArgb(255, 0x30, 0x2B, 0x39);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff",
    };

    private readonly Grid _host;
    private readonly RichEditBox _editor;
    private readonly ImageAdorner _adorner;
    private InMemoryRandomAccessStream? _loadedStream;
    private bool _loading;
    private bool _composing;
    private string _lastEditorText = string.Empty;
    private Windows.Foundation.Point? _pointerPressPoint;
    private int _hoverIndex = -1;
    private int _dropIndex = -1;
    private bool _disposed;
    private ScrollViewer? _viewer;

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
        _editor.SelectionChanged += OnSelectionChanged;
        _editor.Paste += OnPaste;
        _editor.TextCompositionStarted += OnCompositionStarted;
        _editor.TextCompositionEnded += OnCompositionEnded;
        _editor.KeyDown += OnKeyDown;
        _editor.Loaded += OnEditorLoaded;

        // 拖放：只处理图片载荷；文本拖放不干预（交给 RichEdit 原生）。
        _editor.AllowDrop = true;
        _editor.DragEnter += OnDragEnter;
        _editor.DragOver += OnDragOver;
        _editor.DragLeave += OnDragLeave;
        _editor.Drop += OnDrop;

        // 指针事件全程旁听（handledEventsToo: true、且从不设 Handled）：
        // 悬停提示与点击判定都不打断原生选择/光标行为。
        _editor.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnEditorPointerPressed), true);
        _editor.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnEditorPointerReleased), true);
        _editor.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnEditorPointerMoved), true);
        _editor.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnEditorPointerExited), true);

        _host.Children.Add(_editor);

        // 手柄覆盖层要在编辑器之后加入宿主（z 序在上）。
        _adorner = new ImageAdorner(_host, _editor);
        _adorner.ResizeCommitted += OnResizeCommitted;
    }

    /// <summary>仅在正文实际变化或用户执行格式、插图命令时触发。</summary>
    public event EventHandler? UserEdited;

    /// <summary>要提示给用户的一句话（插图/缩放失败等）；窗口把它接到状态栏。</summary>
    public event EventHandler<string>? HintRequested;

    public string PlainText
    {
        get
        {
            _editor.Document.GetText(TextGetOptions.None, out string text);
            return text.TrimEnd('\r', '\n');
        }
    }

    public async Task LoadAsync(byte[] content)
    {
        _loading = true;
        _adorner.Hide();
        try
        {
            if (content.Length == 0)
            {
                _editor.Document.SetText(TextSetOptions.None, string.Empty);
            }
            else
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(content);
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

    public byte[] SaveContent()
    {
        using var stream = new InMemoryRandomAccessStream();
        _editor.Document.SaveToStream(TextGetOptions.FormatRtf, stream);
        stream.Seek(0);
        using var memory = new MemoryStream();
        stream.AsStreamForRead().CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>执行编辑命令：字符格式（bold/italic/underline/strikethrough）、
    /// 段落格式（bullet 分点切换）与待办（todo 行首方框切换）。</summary>
    public void ExecuteCommand(string command)
    {
        switch (command)
        {
            case "bullet":
                ToggleBulletList();
                break;
            case "todo":
                ToggleTodoLines();
                break;
            default:
                if (!TryToggleCharacterFormat(command))
                {
                    return;
                }

                break;
        }

        NotifyUserEdited();
        _editor.Focus(FocusState.Programmatic);
    }

    private bool TryToggleCharacterFormat(string command)
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
                return false;
        }

        _editor.Document.Selection.CharacterFormat = format;
        return true;
    }

    private static SolidColorBrush Transparent() =>
        new(Windows.UI.Color.FromArgb(0, 255, 255, 255));

    /// <summary>编辑区底色：一点点淡紫白薄纱（约两成），毛玻璃基本透出来，反色光标略偏深。</summary>
    private static SolidColorBrush EditorFrost() =>
        new(Windows.UI.Color.FromArgb(0x2E, 0xF7, 0xF3, 0xFD));

    private void OnEditorLoaded(object sender, RoutedEventArgs e) => Safe(nameof(OnEditorLoaded), () =>
    {
        _editor.Loaded -= OnEditorLoaded;
        ApplyScrollBarCursor();

        // 滚动条的滑出/收起行为（全程序通用）。
        ScrollBarReveal.AttachTo(_editor);

        // 滚动或尺寸变化后，图片装饰器的矩形要重测（测不到会自己隐藏）。
        _viewer = ScrollBarReveal.FindAll<ScrollViewer>(_editor).FirstOrDefault();
        if (_viewer is not null)
        {
            _viewer.ViewChanged += OnEditorViewChanged;
        }

        _editor.SizeChanged += OnEditorSizeChanged;
    });

    private void OnEditorViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        Safe(nameof(OnEditorViewChanged), () => _adorner.Refresh());

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e) =>
        Safe(nameof(OnEditorSizeChanged), () => _adorner.Refresh());

    /// <summary>滚动条悬停时不该是文本的 I 形光标——它从 RichEditBox 继承了那个；给滚动条自己设成普通箭头。</summary>
    private void ApplyScrollBarCursor()
    {
        foreach (ScrollBar bar in ScrollBarReveal.FindAll<ScrollBar>(_editor))
        {
            CursorShapes.SetShape(bar, InputSystemCursorShape.Arrow);
        }
    }

    private void OnTextChanged(object sender, RoutedEventArgs args) => Safe(nameof(OnTextChanged), () =>
    {
        if (_loading || _composing)
        {
            return;
        }

        // 图片缩放这类不改变纯文本的编辑也要让装饰器跟上（尺寸/位置都可能变）。
        if (_adorner.IsShowing)
        {
            _adorner.Refresh();
        }

        if (string.Equals(PlainText, _lastEditorText, StringComparison.Ordinal))
        {
            return;
        }

        NotifyUserEdited();
    });

    private void OnCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args) =>
        _composing = true;

    private void OnCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args) =>
        Safe(nameof(OnCompositionEnded), () =>
        {
            _composing = false;

            // 组字期间的 TextChanged 被压下了；结束时补一次比较，确实落了字的算用户编辑。
            if (!string.Equals(PlainText, _lastEditorText, StringComparison.Ordinal))
            {
                NotifyUserEdited();
            }

            if (_adorner.IsShowing)
            {
                _adorner.Refresh();
            }
        });

    private void OnKeyDown(object sender, KeyRoutedEventArgs args) => Safe(nameof(OnKeyDown), () =>
    {
        // 待办行的 Enter：续行/退出在正文层完成，且必须一次原子（撤销步数）。
        if (args.Key == VirtualKey.Enter && !_composing && TryHandleTodoEnter())
        {
            args.Handled = true;
            return;
        }

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
    });

    private void NotifyUserEdited()
    {
        _lastEditorText = PlainText;
        UserEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// XAML 回调跨界到 WinRT 时，逃出去的异常会被"存置"、稍后在原生边界爆成 0xC000027B
    /// 崩掉进程——窗口关闭、文档卸载期间回调仍在触发（选中、文本、指针都在摸半销毁的
    /// 文档）。统一在这里吞掉并记日志：崩不了，VS 输出窗口也留得下线索。
    /// </summary>
    private static void Safe(string member, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[RichEditorHost] {member} 回调异常（已吞掉）：{exception}");
        }
    }

    // ---- 分点（列表） ----

    private void ToggleBulletList()
    {
        ITextSelection selection = _editor.Document.Selection;
        bool isBullet = selection.ParagraphFormat.ListType == MarkerType.Bullet;

        if (!isBullet)
        {
            // 分点与待办互斥：先把选区里待办行的行首符号剥掉，避免"• ☐"叠在一起。
            StripTodoPrefixesInSelection();
            selection = _editor.Document.Selection;
        }

        ITextParagraphFormat format = selection.ParagraphFormat;
        format.ListType = isBullet ? MarkerType.None : MarkerType.Bullet;
        selection.ParagraphFormat = format;
    }

    /// <summary>把选区覆盖的待办行剥成普通行（去掉行首符号并恢复文字颜色）。</summary>
    private void StripTodoPrefixesInSelection()
    {
        _editor.Document.GetText(TextGetOptions.None, out string all);
        List<(int Start, int End)> spans = SelectionLineSpans();
        List<(int Start, int End)> todoSpans = spans
            .Where(span => TodoMarkers.IsTodoLine(Slice(all, span)))
            .ToList();
        if (todoSpans.Count == 0)
        {
            return;
        }

        RichEditTextDocument document = _editor.Document;
        document.BeginUndoGroup();
        try
        {
            // 从后往前改，前面的位置不受影响。
            for (int i = todoSpans.Count - 1; i >= 0; i--)
            {
                (int start, int end) = todoSpans[i];
                document.GetRange(start, start + TodoMarkers.PrefixLength)
                    .SetText(TextSetOptions.None, string.Empty);
                ApplyTodoLineColor(start, end - TodoMarkers.PrefixLength, checkedLine: false);
            }
        }
        finally
        {
            document.EndUndoGroup();
        }
    }

    // ---- 待办 ----

    /// <summary>工具栏「待办」：把选区覆盖的行切换为待办行，或（都已是待办时）整体退出。</summary>
    private void ToggleTodoLines()
    {
        RichEditTextDocument document = _editor.Document;
        document.GetText(TextGetOptions.None, out string all);
        List<(int Start, int End)> spans = SelectionLineSpans();

        List<(int Start, int End)> contentSpans = spans.Where(span => span.End > span.Start).ToList();
        bool removeMode = contentSpans.Count > 0
            && contentSpans.All(span => TodoMarkers.IsTodoLine(Slice(all, span)));

        document.BeginUndoGroup();
        try
        {
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                (int start, int end) = spans[i];
                string line = Slice(all, spans[i]);

                if (removeMode)
                {
                    if (TodoMarkers.IsTodoLine(line))
                    {
                        document.GetRange(start, start + TodoMarkers.PrefixLength)
                            .SetText(TextSetOptions.None, string.Empty);
                        ApplyTodoLineColor(start, end - TodoMarkers.PrefixLength, checkedLine: false);
                    }
                }
                else if (!TodoMarkers.IsTodoLine(line))
                {
                    // 与分点互斥：先退出列表再加前缀。
                    ClearListFormat(start);
                    document.GetRange(start, start).SetText(TextSetOptions.None, TodoMarkers.UncheckedPrefix);
                }
            }
        }
        finally
        {
            document.EndUndoGroup();
        }
    }

    /// <summary>Enter 落在待办行的处理：返回 false 表示按普通换行走。</summary>
    private bool TryHandleTodoEnter()
    {
        ITextSelection selection = _editor.Document.Selection;
        if (selection.StartPosition != selection.EndPosition)
        {
            // 有选区时按普通编辑处理（替换选区、不续行）。
            return false;
        }

        int caret = selection.StartPosition;
        (int lineStart, int lineEnd) = LineBounds(caret);
        string line = LineText(lineStart, lineEnd);

        switch (TodoMarkers.EnterAction(line))
        {
            case TodoEnterAction.Exit:
                // 空待办行：去掉前缀退出，不换行。
                _editor.Document.GetRange(lineStart, lineStart + TodoMarkers.PrefixLength)
                    .SetText(TextSetOptions.None, string.Empty);
                return true;

            case TodoEnterAction.Continue:
                // 换行 + 新前缀一次写入：撤销一步到位。
                _editor.Document.GetRange(caret, caret)
                    .SetText(TextSetOptions.None, "\r" + TodoMarkers.UncheckedPrefix);
                return true;

            default:
                return false;
        }
    }

    /// <summary>点击行首方框切换 ☐/☑：成功返回 true。不动光标——切换前后还原选区（§15.6）。</summary>
    private bool TryToggleTodoAt(int index)
    {
        // 光标落点可能停在方框前/方框上/方框后的空格（最多偏两个字符宽），三个位置都认。
        foreach (int candidate in new[] { index, index - 1, index - 2 })
        {
            if (candidate < 0)
            {
                continue;
            }

            string character = CharAt(candidate);
            bool isBox = character == TodoMarkers.UncheckedSymbol.ToString()
                || character == TodoMarkers.CheckedSymbol.ToString();
            if (!isBox)
            {
                continue;
            }

            bool atLineStart = candidate == 0 || CharAt(candidate - 1) == "\r";
            if (!atLineStart)
            {
                continue;
            }

            ToggleTodoSymbol(candidate);
            return true;
        }

        return false;
    }

    private void ToggleTodoSymbol(int symbolIndex)
    {
        RichEditTextDocument document = _editor.Document;
        ITextSelection selection = document.Selection;
        int savedStart = selection.StartPosition;
        int savedEnd = selection.EndPosition;

        bool wasChecked = CharAt(symbolIndex) == TodoMarkers.CheckedSymbol.ToString();
        string newSymbol = wasChecked
            ? TodoMarkers.UncheckedSymbol.ToString()
            : TodoMarkers.CheckedSymbol.ToString();

        document.BeginUndoGroup();
        try
        {
            document.GetRange(symbolIndex, symbolIndex + 1).SetText(TextSetOptions.None, newSymbol);
            (int lineStart, int lineEnd) = LineBounds(symbolIndex);
            ApplyTodoLineColor(lineStart, lineEnd, checkedLine: !wasChecked);
        }
        finally
        {
            document.EndUndoGroup();
        }

        // 点击方框不该把光标带走：还原点击前（原生已定位的）选区。
        selection.SetRange(savedStart, savedEnd);
    }

    /// <summary>给整行应用勾选态颜色：已勾选灰、未勾选回默认正文色（项目没有其它文字着色功能，可安全复位）。</summary>
    private void ApplyTodoLineColor(int lineStart, int lineEnd, bool checkedLine)
    {
        if (lineEnd <= lineStart)
        {
            return;
        }

        ITextRange range = _editor.Document.GetRange(lineStart, lineEnd);
        ITextCharacterFormat format = range.CharacterFormat;
        format.ForegroundColor = checkedLine ? CheckedLineForeground : DefaultLineForeground;
        range.CharacterFormat = format;
    }

    private void ClearListFormat(int position)
    {
        ITextRange range = _editor.Document.GetRange(position, position);
        ITextParagraphFormat format = range.ParagraphFormat;
        if (format.ListType != MarkerType.Bullet)
        {
            return;
        }

        format.ListType = MarkerType.None;
        range.ParagraphFormat = format;
    }

    // ---- 图片装饰器 ----

    private void OnSelectionChanged(object sender, RoutedEventArgs args) =>
        Safe(nameof(OnSelectionChanged), () =>
        {
            // 加载期间 SetText 造成的选区变化不算用户操作——
            // 否则以图片开头的笔记一打开，手柄就会自己弹出来。
            if (_composing || _loading)
            {
                return;
            }

            ITextSelection selection = _editor.Document.Selection;
            int start = selection.StartPosition;
            int end = selection.EndPosition;

            if (end == start + 1 && IsImageAt(start))
            {
                // 整选一张图片（双击、右键时的原生选择）→ 出手柄。
                _adorner.ShowForImage(start);
                return;
            }

            if (end == start)
            {
                // 单击内嵌图片时 RichEdit 只是把光标落在图片旁边（不整选）——
                // 光标前/后一位是图片就认作"点了图片"，出手柄。
                int near = FindImageNear(start);
                if (near >= 0)
                {
                    _adorner.ShowForImage(near);
                    return;
                }
            }

            _adorner.Hide();
        });

    private void OnResizeCommitted(object? sender, ImageResizeRequest request) =>
        Safe(nameof(OnResizeCommitted), () =>
        {
            RichEditTextDocument document = _editor.Document;
            ITextRange range;
            try
            {
                range = document.GetRange(request.Index, request.Index + 1);
                range.GetText(TextGetOptions.FormatRtf, out string fragment);

                if (RtfPict.TryResize(fragment, request.Width, request.Height, out string resized))
                {
                    // 主路径：按比例改写 \picwgoal/\pichgoal 后回写，像素数据不动。
                    document.BeginUndoGroup();
                    try
                    {
                        range.SetText(TextSetOptions.FormatRtf, resized);
                    }
                    finally
                    {
                        document.EndUndoGroup();
                    }

                    _adorner.Refresh();
                    NotifyUserEdited();
                    _editor.Focus(FocusState.Programmatic);
                    return;
                }

                if (RtfPict.TryExtractImage(fragment, out byte[] pixels, out string blipKind) && RtfPict.IsRaster(blipKind))
                {
                    // 降级路径：取像素删旧图、按新尺寸重插（矢量图没有可重插的位图数据）。
                    _ = ReinsertResizedImageAsync(request.Index, pixels, request.Width, request.Height);
                    return;
                }
            }
            catch (Exception exception)
            {
                HintRequested?.Invoke(this, $"缩放图片失败：{exception.Message}");
            }

            HintRequested?.Invoke(this, "这张图片暂时无法缩放（格式不支持）");
            _adorner.Refresh();
        });

    private async Task ReinsertResizedImageAsync(int index, byte[] pixels, int width, int height)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pixels);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            // 等回来时窗口可能已经关了，别再碰文档。
            if (_disposed)
            {
                return;
            }

            stream.Seek(0);
            RichEditTextDocument document = _editor.Document;
            document.BeginUndoGroup();
            try
            {
                document.GetRange(index, index + 1).SetText(TextSetOptions.None, string.Empty);
                document.GetRange(index, index).InsertImage(
                    width, height, 0, VerticalCharacterAlignment.Baseline, "image", stream);
            }
            finally
            {
                document.EndUndoGroup();
            }

            _adorner.Refresh();
            NotifyUserEdited();
            _editor.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            HintRequested?.Invoke(this, $"缩放图片失败：{exception.Message}");
        }
    }

    // ---- 指针旁听：悬停提示 / 点击切换待办 / 图片命中 ----

    private void OnEditorPointerPressed(object sender, PointerRoutedEventArgs args) =>
        Safe(nameof(OnEditorPointerPressed), () =>
            _pointerPressPoint = args.GetCurrentPoint(_editor).Position);

    private void OnEditorPointerReleased(object sender, PointerRoutedEventArgs args) =>
        Safe(nameof(OnEditorPointerReleased), () =>
        {
            if (_pointerPressPoint is not Windows.Foundation.Point press)
            {
                return;
            }

            _pointerPressPoint = null;
            Windows.Foundation.Point position = args.GetCurrentPoint(_editor).Position;
            double dx = position.X - press.X;
            double dy = position.Y - press.Y;
            if ((dx * dx) + (dy * dy) > ClickSlack * ClickSlack)
            {
                return; // 拖选，不理会
            }

            // 方框判定直接用「点击后的光标落点」——那是引擎对这次点击自己的解释，
            // 比我们再算一遍指针→字符映射少一层口径风险。
            TryToggleTodoAt(_editor.Document.Selection.StartPosition);
        });

    private void OnEditorPointerMoved(object sender, PointerRoutedEventArgs args) =>
        Safe(nameof(OnEditorPointerMoved), () =>
        {
            Windows.Foundation.Point position = args.GetCurrentPoint(_editor).Position;
            int imageIndex = FindImageNear(IndexFromPoint(position));

            if (_adorner.IsSelected)
            {
                return;
            }

            if (imageIndex < 0)
            {
                _hoverIndex = -1;
                _adorner.HideHover();
                return;
            }

            if (imageIndex == _hoverIndex && _adorner.IsShowing)
            {
                return;
            }

            _hoverIndex = imageIndex;
            _adorner.HoverAt(imageIndex);
        });

    private void OnEditorPointerExited(object sender, PointerRoutedEventArgs args) =>
        Safe(nameof(OnEditorPointerExited), () =>
        {
            _hoverIndex = -1;
            _adorner.HideHover();
        });

    private int IndexFromPoint(Windows.Foundation.Point position)
    {
        try
        {
            return _editor.Document.GetRangeFromPoint(position, PointOptions.ClientCoordinates).StartPosition;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private int FindImageNear(int index)
    {
        if (index < 0)
        {
            return -1;
        }

        if (IsImageAt(index))
        {
            return index;
        }

        return index > 0 && IsImageAt(index - 1) ? index - 1 : -1;
    }

    /// <summary>某个字符位置是不是内嵌图片（U+FFFC；拿不准再看该字符的 RTF 片段里有没有 \pict）。</summary>
    private bool IsImageAt(int index)
    {
        if (index < 0)
        {
            return false;
        }

        try
        {
            ITextRange range = _editor.Document.GetRange(index, index + 1);
            range.GetText(TextGetOptions.None, out string text);
            if (text.Length > 0 && text[0] == '￼')
            {
                return true;
            }

            range.GetText(TextGetOptions.FormatRtf, out string rtf);
            return RtfPict.ContainsPict(rtf);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string CharAt(int index)
    {
        _editor.Document.GetRange(index, index + 1).GetText(TextGetOptions.None, out string text);
        return text;
    }

    // ---- 拖放插图 ----

    private void OnDragEnter(object sender, DragEventArgs args) =>
        Safe(nameof(OnDragEnter), () =>
        {
            if (HasImagePayload(args.DataView))
            {
                args.AcceptedOperation = DataPackageOperation.Copy;
            }
        });

    private void OnDragOver(object sender, DragEventArgs args) =>
        Safe(nameof(OnDragOver), () =>
        {
            if (!HasImagePayload(args.DataView))
            {
                return; // 非图片数据不干预，保持原生文本拖放
            }

            args.AcceptedOperation = DataPackageOperation.Copy;

            int index = IndexFromPoint(args.GetPosition(_editor));
            if (index < 0)
            {
                return;
            }

            _dropIndex = index;
            _adorner.ShowDropIndicator(index);

            // 尝试让真实插入符跟着指针走；拖动期间插入符渲染不了就靠指示线兜底。
            try
            {
                _editor.Document.Selection.SetRange(index, index);
            }
            catch (Exception)
            {
                // 忽略：指示线仍在。
            }
        });

    private void OnDragLeave(object sender, DragEventArgs args) =>
        Safe(nameof(OnDragLeave), () =>
        {
            _dropIndex = -1;
            _adorner.HideDropIndicator();
        });

    private void OnDrop(object sender, DragEventArgs args) =>
        Safe(nameof(OnDrop), () =>
        {
            _adorner.HideDropIndicator();
            if (!HasImagePayload(args.DataView))
            {
                return;
            }

            args.Handled = true;
            int index = _dropIndex;
            _dropIndex = -1;
            _ = InsertDroppedImageAsync(args.DataView, index);
        });

    private static bool HasImagePayload(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.Bitmap);

    private async Task InsertDroppedImageAsync(DataPackageView view, int index)
    {
        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
                StorageFile? file = items.OfType<StorageFile>()
                    .FirstOrDefault(candidate => ImageExtensions.Contains(candidate.FileType));
                if (file is null)
                {
                    return;
                }

                using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
                await InsertImageAsync(stream, file.Name, index);
            }
            else if (view.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference reference = await view.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                await InsertImageAsync(stream, "拖入的图片", index);
            }
        }
        catch (Exception exception)
        {
            HintRequested?.Invoke(this, $"插入图片失败：{exception.Message}");
        }
    }

    // ---- 粘贴 ----

    private void OnPaste(object sender, TextControlPasteEventArgs args)
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Bitmap))
            {
                return;
            }

            if (view.Contains(StandardDataFormats.Text) || view.Contains(StandardDataFormats.Rtf))
            {
                return; // 带文字的富内容交给原生粘贴
            }

            // 纯图片：自己走插图逻辑（统一 280px 上限）；Handled 必须同步设置。
            args.Handled = true;
            _ = PasteImageAsync(view);
        }
        catch (Exception)
        {
            // 读剪贴板失败就放行原生粘贴。
        }
    }

    private async Task PasteImageAsync(DataPackageView view)
    {
        try
        {
            RandomAccessStreamReference reference = await view.GetBitmapAsync();
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            await InsertImageAsync(stream, "粘贴的图片", index: -1);
        }
        catch (Exception exception)
        {
            HintRequested?.Invoke(this, $"插入图片失败：{exception.Message}");
        }
    }

    /// <summary>把图片流按 280px 上限转成内嵌 RTF 图。<paramref name="index"/> 为 -1 时插在当前光标处。</summary>
    private async Task InsertImageAsync(IRandomAccessStream stream, string name, int index)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        // 解码是异步的：等回来时窗口可能已经关了，别再碰文档。
        if (_disposed)
        {
            return;
        }

        double scale = Math.Min(1.0, MaxInsertImageEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale));
        stream.Seek(0);

        int at = index >= 0 ? index : _editor.Document.Selection.StartPosition;
        _editor.Document.GetRange(at, at).InsertImage(
            width, height, 0, VerticalCharacterAlignment.Baseline, name, stream);
        NotifyUserEdited();
        _editor.Focus(FocusState.Programmatic);
    }

    // ---- 行/文本小工具 ----

    /// <summary>选区覆盖的每一行（[起点, 终点)，不含行尾 \r）。</summary>
    private List<(int Start, int End)> SelectionLineSpans()
    {
        _editor.Document.GetText(TextGetOptions.None, out string all);
        ITextSelection selection = _editor.Document.Selection;
        int from = Math.Clamp(selection.StartPosition, 0, all.Length);
        int to = Math.Clamp(selection.EndPosition, from, all.Length);

        int lineStart = from;
        while (lineStart > 0 && all[lineStart - 1] != '\r')
        {
            lineStart--;
        }

        var spans = new List<(int, int)>();
        int index = lineStart;
        while (true)
        {
            int lineEnd = index;
            while (lineEnd < all.Length && all[lineEnd] != '\r')
            {
                lineEnd++;
            }

            spans.Add((index, lineEnd));
            if (lineEnd >= to || lineEnd >= all.Length)
            {
                break;
            }

            index = lineEnd + 1;
        }

        return spans;
    }

    private (int Start, int End) LineBounds(int index)
    {
        _editor.Document.GetText(TextGetOptions.None, out string all);
        int start = Math.Clamp(index, 0, all.Length);
        while (start > 0 && all[start - 1] != '\r')
        {
            start--;
        }

        int end = Math.Clamp(index, 0, all.Length);
        while (end < all.Length && all[end] != '\r')
        {
            end++;
        }

        return (start, end);
    }

    private string LineText(int start, int end)
    {
        _editor.Document.GetText(TextGetOptions.None, out string all);
        int from = Math.Clamp(start, 0, all.Length);
        int to = Math.Clamp(end, from, all.Length);
        return all[from..to];
    }

    private static string Slice(string all, (int Start, int End) span) =>
        all[Math.Clamp(span.Start, 0, all.Length)..Math.Clamp(span.End, 0, all.Length)];

    public void Dispose()
    {
        _disposed = true;

        // 拆解发生在窗口关闭/进程退出的当口，任何闪失都会被存置成 0xC000027B
        // 崩掉进程——整体兜住并记日志，绝不让它逃到 WinRT 边界。
        try
        {
            _editor.TextChanged -= OnTextChanged;
            _editor.SelectionChanged -= OnSelectionChanged;
            _editor.Paste -= OnPaste;
            _editor.TextCompositionStarted -= OnCompositionStarted;
            _editor.TextCompositionEnded -= OnCompositionEnded;
            _editor.KeyDown -= OnKeyDown;
            _editor.Loaded -= OnEditorLoaded;
            _editor.DragEnter -= OnDragEnter;
            _editor.DragOver -= OnDragOver;
            _editor.DragLeave -= OnDragLeave;
            _editor.Drop -= OnDrop;
            if (_viewer is not null)
            {
                _viewer.ViewChanged -= OnEditorViewChanged;
                _viewer = null;
            }

            _editor.SizeChanged -= OnEditorSizeChanged;
            _adorner.ResizeCommitted -= OnResizeCommitted;

            _host.Children.Remove(_editor);
            _adorner.Dispose();
            _loadedStream?.Dispose();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[RichEditorHost] Dispose 异常（已吞掉）：{exception}");
        }
    }
}
