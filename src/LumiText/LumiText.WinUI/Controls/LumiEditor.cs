using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using LumiText.Core.Layout;
using LumiText.WinUI.Editing;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace LumiText.WinUI.Controls;

/// <summary>
/// 编辑宿主控件（Phase 2 设计 §9）：组合 <see cref="VirtualizedTextSurface"/>（视口渲染）、
/// <see cref="FlowDocumentRenderer"/>（排版绘制）与 <see cref="EditorCore"/>（编辑内核）。
/// 键入/删除/方向键/选区/光标渲染在本类；IME 组字由 M4 的 TSF 层接入。
/// 与 <see cref="LumiDocumentView"/> 并列，不继承——只读宿主不被编辑能力污染。
/// </summary>
public sealed class LumiEditor : Grid
{
    private static readonly Color InkColor = Color.FromArgb(255, 40, 32, 48);
    private static readonly Color SelectionFill = Color.FromArgb(0x40, 40, 32, 48);

    private readonly ScrollViewer _scroller;
    private readonly Grid _contentGrid;
    private readonly Grid _surfaceHost;
    private readonly VirtualizedTextSurface _surface = new();
    private readonly FlowDocumentRenderer _renderer = new(new Win2DTextMeasurer());
    private readonly DocumentImageStore _imageStore = new(CanvasDevice.GetSharedDevice());
    private EditorCore? _core;
    private Document? _document;
    private TsfManager? _tsf;
    private readonly DispatcherTimer _caretBlink;
    private bool _caretVisible = true;

    public LumiEditor()
    {
        _scroller = new ScrollViewer
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Control 要接收键盘焦点必须 IsTabStop = true（默认 false，Focus() 会静默失败）。
            // CharacterReceived 只发给焦点元素，焦点不落上字符输入永远不触发。
            IsTabStop = true,
        };
        _contentGrid = new Grid();
        // 命中测试铁律（S2 RESULTS §4.1）：Background 必须设全透明画刷，null 不参与命中测试——
        // PointerPressed/Moved/Released 挂在 _surfaceHost 上，没有画刷指针事件永远不触发。
        _surfaceHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _contentGrid.Children.Add(_surfaceHost);
        _scroller.Content = _contentGrid;
        Children.Add(_scroller);

        _surface.RenderViewport = OnRenderViewport;
        _renderer.Images = _imageStore;

        _caretBlink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _caretBlink.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            _surface.Invalidate();
        };

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _scroller.ViewChanged += OnViewChanged;
        // 键盘：PreviewKeyDown 隧道截方向键/快捷键（先于 ScrollViewer 滚动处理）；
        // CharacterReceived 挂 _scroller（Control 可聚焦，冒泡事件必须由焦点元素触发）。
        PreviewKeyDown += OnPreviewKeyDownHandler;
        _scroller.CharacterReceived += OnCharacterReceivedHandler;
        // 鼠标：按下/拖动/松开走指针事件（拖动选择），单击走 Tapped
        _surfaceHost.PointerPressed += OnPointerPressed;
        _surfaceHost.PointerMoved += OnPointerMoved;
        _surfaceHost.PointerReleased += OnPointerReleased;
        Tapped += OnTappedHandler;
        Unloaded += (_, _) =>
        {
            _caretBlink.Stop();
            _tsf?.Dispose();
            _surface.Dispose();
            _imageStore.Dispose();
        };
    }

    /// <summary>排版耗时上报。</summary>
    public event Action<double>? LayoutStatsChanged;

    /// <summary>用户编辑事件（自动保存触发源；组字期由 TSF 层抑制）。</summary>
    public event EventHandler? UserEdited;

    /// <summary>行盒/带/段调试框线。</summary>
    public bool DebugOverlay { get; set; }

    /// <summary>
    /// 宿主窗口（IME 候选窗屏幕定位用）。<see cref="TransformToVisual"/> 只给相对 XAML island
    /// 的坐标，IME 要屏幕物理像素——需要窗口 HWND 经 <c>ClientToScreen</c> 补屏幕原点。
    /// 不设置时 IME 候选窗定位退化为相对窗口原点（位置会偏）。
    /// </summary>
    public Window? HostWindow
    {
        get => _hostWindow;
        set
        {
            if (_hostWindow is not null)
            {
                _hostWindow.Activated -= OnHostWindowActivated;
            }
            _hostWindow = value;
            if (_hostWindow is not null)
            {
                _hostWindow.Activated += OnHostWindowActivated;
            }
        }
    }
    private Window? _hostWindow;

    private void OnHostWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        // 窗口得焦时重设 TSF 文档焦点（与 AssociateFocus 互补，切窗后 IME 不掉回英文）。
        if (args.WindowActivationState != WindowActivationState.Deactivated && _tsf is not null)
        {
            _tsf.Refocus();
        }
    }

    /// <summary>
    /// 工具栏命令（与现产品 RichEditorHost.ExecuteCommand 对齐的命令名）。
    /// 粗/斜/下划/删线映射到 <see cref="ApplyInlineStyleCommand"/>；
    /// bullet/todo 的块结构命令（ToggleBullet/ToggleTodo）尚未实现，暂时 no-op（后续补）。
    /// </summary>
    public void ExecuteCommand(string command)
    {
        if (_core is null)
        {
            return;
        }
        switch (command)
        {
            case "bold":
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Bold));
                break;
            case "italic":
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Italic));
                break;
            case "underline":
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Underline));
                break;
            case "strikethrough":
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Strikethrough));
                break;
            case "bullet":
            case "todo":
                // ToggleBullet/ToggleTodo 块结构命令未实现（后续补），暂 no-op
                break;
        }
    }

    /// <summary>释放 TSF / surface / 图片资源（窗口关闭协议调用；与 Unloaded 清理互补）。</summary>
    public void Dispose()
    {
        _caretBlink.Stop();
        _tsf?.Dispose();
        _tsf = null;
        _surface.Dispose();
        _imageStore.Dispose();
    }

    /// <summary>纯文本投影（§3.4：TodoBlock 带 ☐/☑ 前缀）。</summary>
    public string PlainText => _core?.GetPlainText() ?? string.Empty;

    /// <summary>当前权威文档快照。</summary>
    public Document GetDocument() => _core?.Document ?? _document ?? new Document([]);

    /// <summary>
    /// 序列化权威内容为 v2 JSON 的 UTF-8 字节（IRichTextDocument.SaveContent 的新内核实现）。
    /// VM 不解析字节内容（不透明），只负责塞进 Note.RichTextContent。
    /// </summary>
    public byte[] SaveContent()
    {
        string json = LumiText.Core.Documents.Serialization.DocumentSerializer.Serialize(GetDocument());
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// 从权威格式字节（v2 JSON UTF-8）载入文档。空字节 = 新便签，载入空文档；
    /// 字节非法/版本不支持时抛 <see cref="InvalidDataException"/>——调用方（VM）按容错矩阵决定跳过或降级。
    /// </summary>
    public Task LoadAsync(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            SetDocument(new Document([new ParagraphBlock("")]));
            return Task.CompletedTask;
        }
        string json = System.Text.Encoding.UTF8.GetString(content);
        var doc = LumiText.Core.Documents.Serialization.DocumentSerializer.Deserialize(json)
            ?? throw new InvalidDataException("v2 JSON 反序列化失败（Deserialize 返回 null）。");
        SetDocument(doc);
        return Task.CompletedTask;
    }

    /// <summary>载入文档：排版上屏 + 重建编辑内核。</summary>
    public void SetDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        if (_core is not null)
        {
            _core.DocumentChanged -= OnCoreDocumentChanged;
            _core.SelectionChanged -= OnCoreSelectionChanged;
        }
        _core = new EditorCore(document);
        _core.DocumentChanged += OnCoreDocumentChanged;
        _core.SelectionChanged += OnCoreSelectionChanged;

        // TSF 接入（M4）：组字期抑制 UserEdited，候选窗定位经屏幕坐标回调
        _tsf?.Dispose();
        _tsf = new TsfManager();
        if (_tsf.Initialize(_core))
        {
            _tsf.CompositionStarted += OnCompositionStarted;
            _tsf.CompositionEnded += OnCompositionEnded;
            if (_tsf.TextStore is { } store)
            {
                store.CaretScreenRectProvider = GetCaretScreenRect;
                store.ScreenRectProvider = GetEditorScreenRect;
            }
            // 关联窗口焦点：窗口失焦再切回时 TSF 自动 SetFocus，IME 不掉回英文（M7 实测修复）
            if (HostWindow is not null)
            {
                _tsf.AssociateWindowFocus(WinRT.Interop.WindowNative.GetWindowHandle(HostWindow));
            }
        }

        Relayout();
        _ = WarmupImagesAsync(document);
        _caretBlink.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _surface.Attach(_surfaceHost);
        Relayout();
    }

    private async Task WarmupImagesAsync(Document document)
    {
        await _imageStore.WarmupAsync(
            document.Images,
            () => DispatcherQueue.TryEnqueue(() => _surface.Invalidate()));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _surfaceHost.Height = e.NewSize.Height;
        Relayout();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        _surface.OnScroll((float)_scroller.VerticalOffset);

    private void Relayout()
    {
        if (_document is null || ActualWidth < 50 || ActualHeight < 50)
        {
            return;
        }
        var result = _renderer.UpdateLayout(_document, (float)ActualWidth);
        _contentGrid.Height = Math.Max(result.TotalHeight, ActualHeight);
        _surfaceHost.Height = ActualHeight;
        _surface.SetMetrics((float)ActualWidth, (float)ActualHeight, result.TotalHeight);
        LayoutStatsChanged?.Invoke(_renderer.LastLayoutDuration.TotalMilliseconds);
    }

    private void OnCoreDocumentChanged(object? sender, EventArgs e)
    {
        _document = _core!.Document;
        Relayout();
        // 新增图片（如粘贴插图）后台解码，就绪后补画
        _ = WarmupImagesAsync(_document);
        // 组字期抑制（§6.3）：组字期的文档变化是 IME 组字显示，不触发自动保存
        if (_tsf?.TextStore?.IsComposing != true)
        {
            UserEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCompositionStarted(object? sender, EventArgs e)
    {
        // 组字期光标加粗到 2px（§4.5）
        _surface.Invalidate();
    }

    private void OnCompositionEnded(object? sender, EventArgs e)
    {
        _surface.Invalidate();
    }

    private (int left, int top, int right, int bottom) GetCaretScreenRect(int acpStart, int acpEnd)
    {
        if (_renderer.Current is null || _core is null)
        {
            return (0, 0, 0, 0);
        }
        // ACP → TextPosition → 文档坐标 → 屏幕坐标
        // 简化：用 caret（acpStart 对应位置）的屏幕坐标，候选窗定位够用了
        var pos = AcpToTextPosition(acpStart);
        var caret = CaretGeometryCalculator.GetCaret(_renderer.Current, pos);
        if (caret is not { } c)
        {
            return (0, 0, 0, 0);
        }
        var screenPoint = DocumentToScreen(c.X, c.Y);
        return (screenPoint.x, screenPoint.y, screenPoint.x + 2, screenPoint.y + (int)c.Height);
    }

    private (int left, int top, int right, int bottom) GetEditorScreenRect()
    {
        // 编辑器客户区在屏幕上的物理像素包围盒
        var transform = TransformToVisual(null);
        var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var origin = ClientScreenOrigin();
        int left = origin.x + (int)(point.X * scale);
        int top = origin.y + (int)(point.Y * scale);
        return (left, top,
            left + (int)(ActualWidth * scale), top + (int)(ActualHeight * scale));
    }

    private (int x, int y) DocumentToScreen(float docX, float docY)
    {
        // 文档坐标(DIP) → _surfaceHost 坐标(DIP) → XAML island 坐标(DIP) → 屏幕物理像素。
        // TransformToVisual(null) 只到 island 根（窗口内容区原点），需叠加客户区屏幕原点。
        double hostX = docX;
        double hostY = docY - _scroller.VerticalOffset;
        var transform = _surfaceHost.TransformToVisual(null);
        var point = transform.TransformPoint(new Windows.Foundation.Point(hostX, hostY));
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var origin = ClientScreenOrigin();
        return (origin.x + (int)(point.X * scale), origin.y + (int)(point.Y * scale));
    }

    /// <summary>
    /// 窗口客户区在屏幕上的物理像素原点（经 <c>ClientToScreen(0,0)</c>，含标题栏/边框偏移）。
    /// 无 HostWindow 时退化为 (0,0)——候选窗位置会偏，但不致命。
    /// </summary>
    private (int x, int y) ClientScreenOrigin()
    {
        if (HostWindow is null)
        {
            return (0, 0);
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(HostWindow);
        var pt = new System.Drawing.Point(0, 0);
        ClientToScreen(hwnd, ref pt);
        return (pt.X, pt.Y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ClientToScreen(System.IntPtr hWnd, ref System.Drawing.Point lpPoint);

    private Core.Editing.TextPosition AcpToTextPosition(int acp)
    {
        // 与 TsfTextStore 相同的扁平化映射（简化版：逐块累加）
        var blocks = _core!.Document.Blocks;
        int offset = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            int length = BlockTextOps.GetTextLength(blocks[i]);
            if (acp <= offset + length)
            {
                return new Core.Editing.TextPosition(i, acp - offset);
            }
            offset += length + 1; // +1 for \n
        }
        return new Core.Editing.TextPosition(blocks.Count - 1, 0);
    }

    private void OnCoreSelectionChanged(object? sender, EventArgs e)
    {
        _caretVisible = true;
        _caretBlink.Stop();
        _caretBlink.Start();
        _surface.Invalidate();
        ScrollCaretIntoView();
    }

    private void ScrollCaretIntoView()
    {
        if (_core is null || _renderer.Current is null)
        {
            return;
        }
        var caret = CaretGeometryCalculator.GetCaret(_renderer.Current, _core.Selection.Active);
        if (caret is not { } c)
        {
            return;
        }
        double viewTop = _scroller.VerticalOffset;
        double viewBottom = viewTop + _scroller.ViewportHeight;
        if (c.Y < viewTop)
        {
            _scroller.ChangeView(null, c.Y, null, disableAnimation: true);
        }
        else if (c.Y + c.Height > viewBottom)
        {
            _scroller.ChangeView(null, c.Y + c.Height - _scroller.ViewportHeight, null, disableAnimation: true);
        }
    }

    private void OnPreviewKeyDownHandler(object sender, KeyRoutedEventArgs e)
    {
        if (_core is null || e.Handled)
        {
            return;
        }
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        switch (e.Key)
        {
            case VirtualKey.Back:
                HandleBackspace();
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                HandleDelete();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                _core.ApplyCommand(new SplitBlockCommand(_core.Selection.Active));
                e.Handled = true;
                break;
            case VirtualKey.Left:
                MoveCaret(-1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                MoveCaret(1, shift);
                e.Handled = true;
                break;
            case VirtualKey.B when ctrl:
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Bold));
                e.Handled = true;
                break;
            case VirtualKey.I when ctrl:
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Italic));
                e.Handled = true;
                break;
            case VirtualKey.U when ctrl:
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Underline));
                e.Handled = true;
                break;
            case VirtualKey.Z when ctrl:
                _core.Undo();
                e.Handled = true;
                break;
            case VirtualKey.Y when ctrl:
                _core.Redo();
                e.Handled = true;
                break;
            case VirtualKey.C when ctrl:
                WinClipboard.Copy(_core);
                e.Handled = true;
                break;
            case VirtualKey.X when ctrl:
                WinClipboard.Cut(_core);
                e.Handled = true;
                break;
            case VirtualKey.V when ctrl:
                _ = WinClipboard.PasteAsync(_core);
                e.Handled = true;
                break;
        }
    }

    private void OnCharacterReceivedHandler(object sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_core is null || e.Handled || char.IsControl(e.Character) || e.Character == '\r' || e.Character == '\n')
        {
            return;
        }
        _core.ApplyCommand(new InsertTextCommand(e.Character.ToString()));
        e.Handled = true;
    }

    private void HandleBackspace()
    {
        if (_core!.Selection.IsCollapsed)
        {
            var pos = _core.Selection.Active;
            if (pos.CharIndex > 0)
            {
                _core.ApplyCommand(new DeleteRangeCommand(
                    new TextRange(new TextPosition(pos.BlockIndex, pos.CharIndex - 1), pos)));
            }
            else if (pos.BlockIndex > 0)
            {
                _core.ApplyCommand(new MergeBlockCommand(pos.BlockIndex));
            }
        }
        else
        {
            _core.ApplyCommand(new DeleteRangeCommand(_core.Selection));
        }
    }

    private void HandleDelete()
    {
        if (_core!.Selection.IsCollapsed)
        {
            var pos = _core.Selection.Active;
            var block = _core.Document.Blocks[pos.BlockIndex];
            int length = BlockTextOps.GetTextLength(block);
            if (pos.CharIndex < length)
            {
                _core.ApplyCommand(new DeleteRangeCommand(
                    new TextRange(pos, new TextPosition(pos.BlockIndex, pos.CharIndex + 1))));
            }
            else if (pos.BlockIndex < _core.Document.Blocks.Count - 1)
            {
                _core.ApplyCommand(new MergeBlockCommand(pos.BlockIndex + 1));
            }
        }
        else
        {
            _core.ApplyCommand(new DeleteRangeCommand(_core.Selection));
        }
    }

    private void MoveCaret(int direction, bool extend)
    {
        var pos = _core!.Selection.Active;
        int newChar = pos.CharIndex + direction;
        var block = _core.Document.Blocks[pos.BlockIndex];
        int length = BlockTextOps.GetTextLength(block);
        TextPosition newPos;
        if (newChar < 0)
        {
            if (pos.BlockIndex == 0)
            {
                return;
            }
            var prevBlock = _core.Document.Blocks[pos.BlockIndex - 1];
            newPos = new TextPosition(pos.BlockIndex - 1, BlockTextOps.GetTextLength(prevBlock));
        }
        else if (newChar > length)
        {
            if (pos.BlockIndex >= _core.Document.Blocks.Count - 1)
            {
                return;
            }
            newPos = new TextPosition(pos.BlockIndex + 1, 0);
        }
        else
        {
            newPos = new TextPosition(pos.BlockIndex, newChar);
        }

        if (extend)
        {
            _core.SetSelection(new TextRange(_core.Selection.Anchor, newPos));
        }
        else
        {
            _core.SetSelection(TextRange.Collapse(newPos));
        }
    }

    private void OnTappedHandler(object sender, TappedRoutedEventArgs e)
    {
        if (_core is null || _renderer.Current is null)
        {
            return;
        }
        _scroller.Focus(FocusState.Pointer); // CharacterReceived 需要焦点元素
        var point = e.GetPosition(_surfaceHost);
        var docY = (float)(point.Y + _scroller.VerticalOffset);
        var hit = _renderer.Current.HitTest((float)point.X, docY);
        if (hit.Found)
        {
            _core.SetSelection(TextRange.Collapse(hit.CaretPosition));
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // 拖动选择（§4.1：按下落点 → 拖动扩展选区 → 松开定格）
    // ------------------------------------------------------------------

    private bool _isDraggingSelection;
    private TextPosition _dragAnchor;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_core is null || _renderer.Current is null)
        {
            return;
        }
        _scroller.Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(_surfaceHost).Position;
        var hit = _renderer.Current.HitTest((float)point.X, (float)(point.Y + _scroller.VerticalOffset));
        if (hit.Found)
        {
            _dragAnchor = hit.CaretPosition;
            _core.SetSelection(TextRange.Collapse(_dragAnchor));
            _isDraggingSelection = true;
            _surfaceHost.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelection || _core is null || _renderer.Current is null)
        {
            return;
        }
        var point = e.GetCurrentPoint(_surfaceHost).Position;
        var hit = _renderer.Current.HitTest((float)point.X, (float)(point.Y + _scroller.VerticalOffset));
        if (hit.Found)
        {
            _core.SetSelection(new TextRange(_dragAnchor, hit.CaretPosition));
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSelection)
        {
            _isDraggingSelection = false;
            _surfaceHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnRenderViewport(CanvasDrawingSession session, Windows.Foundation.Rect viewport, float scale)
    {
        if (_renderer.Current is null)
        {
            return;
        }

        // 选区高亮（文字下层，§4.5）
        if (_core is not null && !_core.Selection.IsCollapsed)
        {
            var rects = CaretGeometryCalculator.GetSelectionRects(_renderer.Current, _core.Selection);
            foreach (var r in rects)
            {
                session.FillRectangle(new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height), SelectionFill);
            }
        }

        _renderer.Render(session, _renderer.Current, viewport, InkColor, DebugOverlay);

        // 光标（最上层）
        if (_core is not null && _caretVisible)
        {
            var caret = CaretGeometryCalculator.GetCaret(_renderer.Current, _core.Selection.Active);
            if (caret is { } c)
            {
                session.FillRectangle(
                    new Windows.Foundation.Rect(c.X, c.Y, c.Width, c.Height), InkColor);
            }
        }
    }
}
