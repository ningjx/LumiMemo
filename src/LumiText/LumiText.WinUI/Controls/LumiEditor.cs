using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using LumiText.Core.Documents;
using LumiText.Core.Editing;
using LumiText.Core.Editing.Commands;
using LumiText.Core.Layout;
using LumiText.WinUI.Editing;
using LumiText.WinUI.Rendering;
using LumiText.WinUI.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
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

    /// <summary>上下移动的期望列（文档坐标 X，Phase 3 M6 §6.4）；null = 下一趟重取。</summary>
    private float? _goalCaretX;

    /// <summary>期望列对应的落点：光标落点与它不同（被点击/打字/左右键改过）时重取期望列。</summary>
    private TextPosition _goalPosition;
    private readonly Microsoft.UI.Input.InputCursor _ibeamCursor;
    private readonly Microsoft.UI.Input.InputCursor _handCursor;

    // 勾选动画（Phase 3 M3）：块索引 + 起时；16ms 逐帧重绘，播完自停
    private readonly DispatcherTimer _checkAnimTimer;

    /// <summary>悬停中的 todo 块索引（-1 = 无），驱动光标与悬停高亮。</summary>
    private int _hoverTodoBlock = -1;

    private int _animatedTodoBlock = -1;
    private long _checkAnimStartTicks;
    private const int CheckAnimationMs = 160;

    // ---- 图片交互（Phase 3 M4）----
    // 覆盖层 = 滚动内容内、surface 之上的 Canvas：虚线框 + 八手柄 + 尺寸标签。
    // Canvas 不设 Background（空白处不吃指针），只有手柄元素吃事件——覆盖层的一贯纪律。
    private readonly Canvas _imageOverlay = new();
    private readonly Rectangle _imageOutline = new();
    private readonly Dictionary<ImageHandle, Border> _imageHandles = [];
    private readonly Border _imageSizeLabel = new();
    private readonly TextBlock _imageSizeLabelText = new();

    /// <summary>选中的图片块索引（-1 = 未选中）。</summary>
    private int _selectedImageBlock = -1;

    /// <summary>悬停的图片块索引（-1 = 无），仅显示细框提示。</summary>
    private int _hoverImageBlock = -1;

    /// <summary>拖动改锚点中的图片块索引（-1 = 未拖动）。</summary>
    private int _dragImageBlock = -1;

    /// <summary>拖动预览：自由矩形（Rect 直给路径；Relayout 时替换该浮动的定位方式）。</summary>
    private LayoutRect? _dragFreeRect;

    private FloatAnchor _pendingAnchor = new(0, 0);
    private FloatSide _pendingSide = FloatSide.Right;
    private float _pointerViewportY;
    private float _pointerDocX;

    // 拖动时抓取点相对图片左上角的偏移与图片尺寸（跟手移动用）
    private float _dragGrabOffsetX;
    private float _dragGrabOffsetY;
    private float _dragSizeW;
    private float _dragSizeH;

    /// <summary>是否已越过拖动阈值（按下后移动 &gt; 4dip 才把图片提起来，单击只选中）。</summary>
    private bool _dragImageActive;
    private float _dragPressX;
    private float _dragPressY;

    // 拖动到视口边缘时的自动滚动（16ms 一帧、8dip/帧）
    private readonly DispatcherTimer _imageScrollTimer;
    private float _autoScrollDelta;

    // 缩放手柄拖动（覆盖层只动预览几何，松手才提交 + 重排；左/上侧手柄松手按新左上角重锚）
    private ImageHandle _resizeHandle = ImageHandle.None;
    private LayoutRect _resizeStartRect;
    private LayoutRect? _resizePreview;

    /// <summary>重锚用的自然版面（「去掉这张图」，手势内复用，见 <see cref="NaturalLayoutForResize"/>）。</summary>
    private LayoutResult? _resizeNaturalLayout;

    /// <summary>OS 拖放插图的落点指示（拖入悬停期间显示竖线）。</summary>
    private Core.Editing.TextPosition? _dropIndicator;

    private const float HandleSize = 11f;
    private static readonly Color OutlineColor = Color.FromArgb(0xCC, 0x7A, 0x6B, 0xB5);
    private static readonly Color HandleFill = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color HandleBorderColor = Color.FromArgb(0xCC, 0x5B, 0x4F, 0xA8);
    private static readonly Color SizeLabelBackground = Color.FromArgb(0xCC, 0x33, 0x30, 0x3B);

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
        BuildImageOverlay();
        _scroller.Content = _contentGrid;
        Children.Add(_scroller);

        // 文本区光标：I 形（WinUI 3 经 ProtectedCursor 设置，Phase 2 §11 V-CU1 已查证）；
        // 悬停待办复选框时切手型（Phase 3 M3）。ScrollViewer 的滚动条模板件自带光标、自会覆盖。
        _ibeamCursor = Microsoft.UI.Input.InputSystemCursor.Create(
            Microsoft.UI.Input.InputSystemCursorShape.IBeam);
        _handCursor = Microsoft.UI.Input.InputSystemCursor.Create(
            Microsoft.UI.Input.InputSystemCursorShape.Hand);
        ProtectedCursor = _ibeamCursor;

        _surface.RenderViewport = OnRenderViewport;
        _renderer.Images = _imageStore;

        _caretBlink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _caretBlink.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            _surface.Invalidate();
        };

        _checkAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _checkAnimTimer.Tick += OnCheckAnimTick;

        _imageScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _imageScrollTimer.Tick += OnImageAutoScrollTick;

        // OS 拖放插图（Phase 3 M4）：文件/位图拖入 → 落点锚定插图
        AllowDrop = true;
        DragOver += OnDragOverHandler;
        DragLeave += OnDragLeaveHandler;
        Drop += OnDropHandler;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _scroller.ViewChanged += OnViewChanged;
        // 编辑器得焦时也重设 TSF 焦点：点击进入文本、XAML 焦点恢复都会走这里，
        // 与窗口 Activated 的重设形成多层兜底（Phase 3 修复：重开窗口后 IME 绕开本店）。
        _scroller.GotFocus += (_, _) => RefocusTsf();
        // 键盘：PreviewKeyDown 隧道截方向键/快捷键（先于 ScrollViewer 滚动处理）；
        // CharacterReceived 挂 _scroller（Control 可聚焦，冒泡事件必须由焦点元素触发）。
        PreviewKeyDown += OnPreviewKeyDownHandler;
        _scroller.CharacterReceived += OnCharacterReceivedHandler;
        // 鼠标：按下/拖动/松开走指针事件（拖动选择），单击走 Tapped
        _surfaceHost.PointerPressed += OnPointerPressed;
        _surfaceHost.PointerMoved += OnPointerMoved;
        _surfaceHost.PointerReleased += OnPointerReleased;
        // 指针移出编辑器：清悬停态（手型光标/高亮复位）
        _surfaceHost.PointerExited += (_, _) => UpdateHover(-1f, -1f);
        Tapped += OnTappedHandler;
        Unloaded += (_, _) =>
        {
            _caretBlink.Stop();
            _checkAnimTimer.Stop();
            _imageScrollTimer.Stop();
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

    /// <summary>诊断日志前缀：宿主窗口句柄（区分多窗口日志，Phase 3 排查用）。</summary>
    private string LogTag
    {
        get
        {
            try
            {
                return HostWindow is null
                    ? "?"
                    : WinRT.Interop.WindowNative.GetWindowHandle(HostWindow).ToString("X4");
            }
            catch
            {
                return "?";
            }
        }
    }

    private void OnHostWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }
        RefocusTsf();
    }

    /// <summary>
    /// 重设 TSF 文档焦点：立即一次 + 下一个派发周期补一次，并启动短延时自愈（见
    /// <see cref="RebuildTsfStack"/>）。SetFocus 同文档幂等，成本可忽略。
    /// </summary>
    private void RefocusTsf()
    {
        if (_tsf is null)
        {
            return;
        }
        _tsf.Refocus();
        DispatcherQueue.TryEnqueue(() => _tsf?.Refocus());
        _tsfRefocusTimer ??= CreateTsfRefocusTimer();
        _tsfRefocusTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTsfRefocusTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        // 激活风暴平息后重建 TSF 栈（自愈）：实测「关闭便签重开」能恢复中文输入，
        // 说明坏掉的是这个窗口实例里的 TSF 输入通路；在编辑器内等价重建，不必重开窗口。
        timer.Tick += (_, _) => RebuildTsfStack();
        return timer;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tsfRefocusTimer;

    /// <summary>
    /// 重建本编辑器的 TSF 栈（Phase 3 自愈）：便签窗口在「同进程另一窗口在前台时被重新激活」
    /// 之后，IME 会停止处理该窗口的按键（打拼音出英文、Shift/切语言都无效），而窗口前台、
    /// 键盘焦点、TSF 文档焦点、键盘布局、IME 模式/开关等全部状态读取均正常。
    /// 实测两条：①「关闭便签重开」可恢复；② 在编辑器内重建 TSF 栈同样恢复（本方法）。
    /// 故按「该窗口实例的 TSF 输入通路失效」处理：丢弃旧文档/上下文重建一套，代价约 1ms。
    /// 组字进行中跳过（那是通路正常的表现，重建反而会打断组字；反之通路失效时不会有组字）。
    /// </summary>
    private void RebuildTsfStack()
    {
        if (_tsf is null || _core is null || _tsf.TextStore?.IsComposing == true)
        {
            return;
        }
        AttachTsf();
    }

    /// <summary>
    /// 建/重建 TSF 栈：丢弃旧 ThreadMgr 文档与上下文，新建一套并接线
    /// （组字期事件、候选窗定位回调、窗口焦点关联）。SetDocument 与自愈重建共用。
    /// </summary>
    private void AttachTsf()
    {
        if (_core is null)
        {
            return;
        }
        _tsf?.Dispose();
        _tsf = new TsfManager { Tag = LogTag };
        bool tsfReady = _tsf.Initialize(_core);
        if (!tsfReady)
        {
            // 指示灯：TSF 初始化失败（无 IME 环境不致命，编辑器仍可键盘输入）
            System.Diagnostics.Debug.WriteLine($"[TSF {LogTag}] TSF 初始化失败");
            return;
        }
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

    /// <summary>
    /// 工具栏命令（命令名与主程序工具栏对齐）。
    /// 粗/斜/下划/删线映射到 <see cref="ApplyInlineStyleCommand"/>；
    /// bullet/todo 映射到 <see cref="ToggleBulletCommand"/>/<see cref="ToggleTodoCommand"/>。
    /// 执行后把焦点要回编辑区（Phase 3 打磨）：按钮还拿着焦点时按回车会「再触发一次按钮」，
    /// 表现为刚输入文字就被取消标题/取消分点。
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
                _core.ApplyCommand(new ToggleBulletCommand(_core.Selection));
                break;
            case "todo":
                _core.ApplyCommand(new ToggleTodoCommand(_core.Selection));
                break;
            case "h1":
                _core.ApplyCommand(new SetHeadingLevelCommand(_core.Selection, 1));
                break;
            case "h2":
                _core.ApplyCommand(new SetHeadingLevelCommand(_core.Selection, 2));
                break;
            case "h3":
                _core.ApplyCommand(new SetHeadingLevelCommand(_core.Selection, 3));
                break;
        }
        FocusEditor();
    }

    /// <summary>把键盘焦点交还编辑区（工具栏点完按钮后必须调，否则回车会重复触发按钮）。</summary>
    public void FocusEditor() => _scroller.Focus(FocusState.Programmatic);

    /// <summary>光标所在块（无文档时为 null）。</summary>
    private Block? CaretBlock
    {
        get
        {
            if (_core is null || _core.Document.Blocks.Count == 0)
            {
                return null;
            }
            int index = Math.Clamp(_core.Selection.Active.BlockIndex, 0, _core.Document.Blocks.Count - 1);
            return _core.Document.Blocks[index];
        }
    }

    /// <summary>
    /// 光标所在块的标题级别（0 = 非标题），供工具栏按钮态联动（Phase 3 M2 §6.1）。
    /// </summary>
    public int CaretHeadingLevel => CaretBlock is HeadingBlock h ? h.Level : 0;

    /// <summary>光标所在块是否分点段落（工具栏按钮态联动，Phase 3 打磨）。</summary>
    public bool CaretIsBullet => CaretBlock is ParagraphBlock { IsBullet: true };

    /// <summary>光标所在块是否待办块（工具栏按钮态联动，Phase 3 打磨）。</summary>
    public bool CaretIsTodo => CaretBlock is TodoBlock;

    /// <summary>光标所在块变化（选区或文档变化）——工具栏按此刷新按钮态。</summary>
    public event EventHandler? CaretBlockChanged;

    /// <summary>
    /// 文字底色（Phase 3 打磨，取代原块级背景）：对当前选区覆盖的文字设置行内背景（null = 清除）。
    /// 选区坍缩时不动（没有选中文字可上色）；设置后把焦点要回编辑区。
    /// </summary>
    public void SetInlineBackground(Color32? color)
    {
        if (_core is null || _core.Selection.IsCollapsed)
        {
            return;
        }
        _core.ApplyCommand(new SetInlineBackgroundCommand(_core.Selection, color));
        FocusEditor();
    }

    /// <summary>释放 TSF / surface / 图片资源（窗口关闭协议调用；与 Unloaded 清理互补）。</summary>
    public void Dispose()
    {
        _caretBlink.Stop();
        _checkAnimTimer.Stop();
        _imageScrollTimer.Stop();
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
        AttachTsf();

        // 换文档：清掉全部图片交互态（选中/悬停/拖动/缩放预览）
        _selectedImageBlock = -1;
        _hoverImageBlock = -1;
        _dragImageBlock = -1;
        _dragFreeRect = null;
        _resizePreview = null;
        _resizeHandle = ImageHandle.None;

        Relayout();
        _ = WarmupImagesAsync(document);
        _caretBlink.Start();
        // 载入即刷新工具栏按钮态（光标落在标题块时按钮应立即可见为按下）
        CaretBlockChanged?.Invoke(this, EventArgs.Empty);
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
        // 拖动改锚点期间：该图片以「自由矩形」参与排版（Rect 直给路径，跟手移动）；
        // 松手时按其左上角命中的字符写回锚点 + 横向自由偏移，之后重排跟着锚点走。
        var floats = _document.GetFloats();
        if (_dragFreeRect is { } dragRect && _dragImageBlock >= 0)
        {
            // 预览与落点一致：拖动中的图片按"紧跟锚字符"的语义排（无外边距），
            // 否则松手瞬间文字还会再挪 4dip（排除区外扩）
            floats = [.. floats.Select(f => f.Id == _dragImageBlock
                ? f with { Anchor = null, AnchorToChar = false, Rect = dragRect, Margin = 0f }
                : f)];
        }
        var result = _renderer.UpdateLayout(_document.Blocks, floats, (float)ActualWidth);
        _contentGrid.Height = Math.Max(result.TotalHeight, ActualHeight);
        _surfaceHost.Height = ActualHeight;
        _surface.SetMetrics((float)ActualWidth, (float)ActualHeight, result.TotalHeight);
        UpdateImageOverlay();
        LayoutStatsChanged?.Invoke(_renderer.LastLayoutDuration.TotalMilliseconds);
    }

    private void OnCoreDocumentChanged(object? sender, EventArgs e)
    {
        _document = _core!.Document;
        Relayout();
        // 新增图片（如粘贴插图）后台解码，就绪后补画
        _ = WarmupImagesAsync(_document);
        CaretBlockChanged?.Invoke(this, EventArgs.Empty);
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
        CaretBlockChanged?.Invoke(this, EventArgs.Empty);
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
                HandleEnter();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                // Esc 退出分点/勾选（Phase 3 打磨）：没有可清的不消费按键，留给别的用途
                e.Handled = ClearListMarks();
                break;
            case VirtualKey.Left:
                MoveCaret(-1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                MoveCaret(1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveVertical(-1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveVertical(1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Home when ctrl:
                MoveTo(CaretNavigator.DocumentEdge(_core.Document.Blocks, toEnd: false), shift);
                e.Handled = true;
                break;
            case VirtualKey.End when ctrl:
                MoveTo(CaretNavigator.DocumentEdge(_core.Document.Blocks, toEnd: true), shift);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveToLineEdge(toEnd: false, shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveToLineEdge(toEnd: true, shift);
                e.Handled = true;
                break;
            case VirtualKey.B when ctrl:
                _core.ApplyCommand(new ApplyInlineStyleCommand(_core.Selection, InlineStyleFlag.Bold));
                e.Handled = true;
                break;
            case VirtualKey.A when ctrl:
                SelectAll();
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
        // 指示灯（Phase 3 修复配套）：字符绕过 TSF 直接进来时留一行日志——
        // 非 ASCII 直入或 TSF 焦点不在本店，都是「输入通路异常」的信号；
        // 正常按键（ASCII、焦点在本店）不打印，日常日志保持干净。
        if (_tsf is { } tsf)
        {
            bool focusOurs = tsf.HasFocus;
            if (!focusOurs || !char.IsAscii(e.Character))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TSF {LogTag}] 字符 '{e.Character}' 直入：TSF 焦点是否在本店={focusOurs}，" +
                    $"组字计数={tsf.CompositionStartCount}");
            }
            if (!focusOurs)
            {
                tsf.Refocus();
            }
        }
        _core.ApplyCommand(new InsertTextCommand(e.Character.ToString()));
        e.Handled = true;
    }

    /// <summary>
    /// Enter：普通分块（标题内回车 → 新块为正文段，见 <see cref="SplitBlockCommand"/>）；
    /// <b>空的分点/勾选行改为退出列表</b>（Phase 3 打磨，Word 同款）——空行上回车否则会
    /// 无限续出新的分点/勾选框，等于没有出口。
    /// </summary>
    private void HandleEnter()
    {
        if (_core is null)
        {
            return;
        }
        var caret = _core.Selection.Active;
        var blocks = _core.Document.Blocks;
        if (_core.Selection.IsCollapsed
            && caret.BlockIndex >= 0 && caret.BlockIndex < blocks.Count
            && blocks[caret.BlockIndex] is TodoBlock or ParagraphBlock { IsBullet: true }
            && BlockTextOps.GetTextLength(blocks[caret.BlockIndex]) == 0)
        {
            _core.ApplyCommand(new ClearListMarksCommand(TextRange.Collapse(caret)));
            return;
        }
        _core.ApplyCommand(new SplitBlockCommand(caret));
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
            else if (_core.Document.Blocks[pos.BlockIndex] is TodoBlock
                or ParagraphBlock { IsBullet: true })
            {
                // 行首退格：先退出本行的分点/勾选（Phase 3 打磨，与 Esc / 按钮再按一次同一语义）。
                // 再按一次才与前块合并——Word 同款：第一下退出列表、第二下并段。
                _core.ApplyCommand(new ClearListMarksCommand(TextRange.Collapse(pos)));
            }
            else if (pos.BlockIndex > 0)
            {
                _core.ApplyCommand(new MergeBlockCommand(pos.BlockIndex));
            }
            else if (_core.Document.Blocks[0] is HeadingBlock)
            {
                // 首块标题行首退格 → 降级为正文段（Phase 3 §6.1）
                _core.ApplyCommand(new SetHeadingLevelCommand(TextRange.Collapse(pos), 0));
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
            MoveTo(newPos, extend: true);
        }
        else
        {
            MoveTo(newPos, extend: false);
        }
    }

    /// <summary>
    /// 上下移动（Phase 3 M6 §6.4）：按<b>视觉行</b>走（行盒序 = 视觉序，跨块自然成立），
    /// 目标行内按<b>期望列</b>（goal-X）命中字符——一趟连续上下移动里列宽固定，
    /// 短行穿过时不会过早贴边；光标被点击/打字/左右键改动过（落点与上次不同）就按当前位置重取。
    /// </summary>
    private void MoveVertical(int direction, bool extend)
    {
        if (_core is null || _renderer.Current is not { } layout)
        {
            return;
        }
        var position = _core.Selection.Active;
        float? goal = _goalCaretX;
        if (goal is null || position != _goalPosition)
        {
            goal = CaretGeometryCalculator.GetCaret(layout, position)?.X ?? 0f;
            _goalCaretX = goal;
        }
        var target = CaretNavigator.Vertical(layout, position, goal.Value, down: direction > 0);
        if (target == position)
        {
            return; // 首/末行：原地不动（期望列留给下一次）
        }
        _goalPosition = target;
        MoveTo(target, extend);
    }

    /// <summary>Home/End：当前视觉行的行首/行末。</summary>
    private void MoveToLineEdge(bool toEnd, bool extend)
    {
        if (_core is null || _renderer.Current is not { } layout)
        {
            return;
        }
        MoveTo(CaretNavigator.LineEdge(layout, _core.Selection.Active, toEnd), extend);
    }

    /// <summary>设置光标（Shift 扩选：锚点保持、活动端移动）。</summary>
    private void MoveTo(TextPosition position, bool extend)
    {
        if (_core is null)
        {
            return;
        }
        _core.SetSelection(extend
            ? new TextRange(_core.Selection.Anchor, position)
            : TextRange.Collapse(position));
    }

    /// <summary>
    /// Esc 退出分点/勾选（Phase 3 打磨）：选区覆盖的段落（无选区 = 光标所在块）里只要有分点或
    /// 勾选框就清掉——只清不加（<see cref="ClearListMarksCommand"/>）。
    /// 返回是否有可清的（没有则不消费按键）。
    /// </summary>
    private bool ClearListMarks()
    {
        if (_core is null)
        {
            return false;
        }
        if (_tsf is { CompositionStartCount: > 0 })
        {
            return false; // 组字中：Esc 先留给输入法（取消候选），不动文档
        }
        var blocks = _core.Document.Blocks;
        var (start, end) = (_core.Selection.Start, _core.Selection.End);
        bool hasMark = false;
        for (int i = Math.Max(0, start.BlockIndex);
            i <= Math.Min(end.BlockIndex, blocks.Count - 1);
            i++)
        {
            if (blocks[i] is TodoBlock || blocks[i] is ParagraphBlock { IsBullet: true })
            {
                hasMark = true;
                break;
            }
        }
        if (!hasMark)
        {
            return false;
        }
        _core.ApplyCommand(new ClearListMarksCommand(_core.Selection));
        return true;
    }

    /// <summary>全选（Ctrl+A）：首个可放光标块的行首 → 末个可放光标块的行尾（图片块跳过）。</summary>
    private void SelectAll()
    {
        if (_core is null)
        {
            return;
        }
        var blocks = _core.Document.Blocks;
        _core.SetSelection(new TextRange(
            CaretNavigator.DocumentEdge(blocks, toEnd: false),
            CaretNavigator.DocumentEdge(blocks, toEnd: true)));
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
    // 指针交互（§4.1）：单击落点/拖动扩选/双击选词/三击选段；
    // todo 块首行缩进区（复选框）点击切换勾选。
    // ------------------------------------------------------------------

    private enum DragGranularity
    {
        /// <summary>字符级（单击按下后拖动）。</summary>
        Char,

        /// <summary>词级（双击后拖动：锚与游标各自扩到词界）。</summary>
        Word,

        /// <summary>段落级（三击后拖动：整段扩选）。</summary>
        Block,
    }

    private bool _isDraggingSelection;
    private DragGranularity _dragGranularity = DragGranularity.Char;
    private TextPosition _dragAnchor;
    private TextPosition _wordAnchorEnd; // 词级/段级拖动的锚区另一端（越过锚点时翻转向量用）
    private bool _wordDragFlipped;

    // 双击/三击：PointerUpdateKind 没有双击语义（官方文档确认成员只有 Pressed/Released），
    // 按下次数自维护——近距离 + 时间窗内连续按下计 2/3 次；窗口外归 1。
    private const int MultiClickMs = 500;
    private const float MultiClickRadius = 4f;
    private int _consecutivePresses;
    private long _lastPressTimestamp;
    private float _lastPressX;
    private float _lastPressY;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_core is null || _renderer.Current is null)
        {
            return;
        }
        _scroller.Focus(FocusState.Pointer);
        var currentPoint = e.GetCurrentPoint(_surfaceHost);
        var point = currentPoint.Position;
        float docX = (float)point.X;
        float docY = (float)(point.Y + _scroller.VerticalOffset);

        // 复选框点击：todo 块首行、按下点落在缩进区（行盒 X 到缩进起点）→ 切勾选，不动光标
        if (currentPoint.Properties.IsLeftButtonPressed && TryToggleTodoAt(docX, docY))
        {
            e.Handled = true;
            return;
        }

        // 浮动图片：命中 → 选中并进入拖动改锚点（Phase 3 M4）；命中的是图片就不再落光标
        if (currentPoint.Properties.IsLeftButtonPressed && HitTestImage(docX, docY) is var imageBlock
            && imageBlock >= 0)
        {
            e.Handled = true;
            _surfaceHost.CapturePointer(e.Pointer);
            SelectImage(imageBlock);
            BeginImageDrag(imageBlock, docX, docY);
            return;
        }
        if (_selectedImageBlock >= 0)
        {
            SelectImage(-1); // 点到别处：取消图片选中
        }

        var hit = _renderer.Current.HitTest(docX, docY);
        if (!hit.Found)
        {
            return;
        }
        e.Handled = true;
        _surfaceHost.CapturePointer(e.Pointer);

        long now = Environment.TickCount64;
        bool inWindow = now - _lastPressTimestamp <= MultiClickMs
            && Math.Abs(docX - _lastPressX) <= MultiClickRadius
            && Math.Abs(docY - _lastPressY) <= MultiClickRadius;
        _consecutivePresses = inWindow ? (_consecutivePresses % 3) + 1 : 1;
        _lastPressTimestamp = now;
        _lastPressX = docX;
        _lastPressY = docY;

        switch (_consecutivePresses)
        {
            case 2:
            {
                var range = WordRangeAt(hit.CaretPosition);
                _dragAnchor = range.Start;
                _wordAnchorEnd = range.End;
                _wordDragFlipped = false;
                _dragGranularity = DragGranularity.Word;
                _isDraggingSelection = true;
                _core.SetSelection(range);
                break;
            }
            case 3:
            {
                var range = BlockRangeAt(hit.CaretPosition);
                _dragAnchor = range.Start;
                _wordAnchorEnd = range.End;
                _wordDragFlipped = false;
                _dragGranularity = DragGranularity.Block;
                _isDraggingSelection = true;
                _core.SetSelection(range);
                break;
            }
            default:
                _dragAnchor = hit.CaretPosition;
                _dragGranularity = DragGranularity.Char;
                _isDraggingSelection = true;
                _core.SetSelection(TextRange.Collapse(_dragAnchor));
                break;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_core is null || _renderer.Current is null)
        {
            return;
        }
        var point = e.GetCurrentPoint(_surfaceHost).Position;
        float docX = (float)point.X;
        float docY = (float)(point.Y + _scroller.VerticalOffset);
        _pointerDocX = docX;
        _pointerViewportY = (float)point.Y;

        // 拖动改锚点（Phase 3 M4）：更新预览（跨行/换侧才重排）+ 视口边缘自动滚动
        if (_dragImageBlock >= 0)
        {
            UpdateImageDrag(docX, docY);
            UpdateImageAutoScroll((float)point.Y);
            e.Handled = true;
            return;
        }

        if (!_isDraggingSelection)
        {
            // 非拖动：悬停态检测（todo 复选框 / 浮动图片 → 手型光标 + 高亮，Phase 3 M3/M4）
            UpdateHover(docX, docY);
            return;
        }

        var hit = _renderer.Current.HitTest(docX, docY);
        if (!hit.Found)
        {
            return;
        }
        e.Handled = true;

        switch (_dragGranularity)
        {
            case DragGranularity.Char:
                _core.SetSelection(new TextRange(_dragAnchor, hit.CaretPosition));
                break;
            case DragGranularity.Word:
            case DragGranularity.Block:
            {
                var cursor = _dragGranularity == DragGranularity.Word
                    ? WordRangeAt(hit.CaretPosition)
                    : BlockRangeAt(hit.CaretPosition);
                // 越过锚区时翻转：选区从「锚区.起 → 游标区.止」翻成「锚区.止 → 游标区.起」
                bool flipped = cursor.Start < _dragAnchor;
                if (flipped != _wordDragFlipped)
                {
                    _wordDragFlipped = flipped;
                }
                var from = _wordDragFlipped ? _wordAnchorEnd : _dragAnchor;
                var to = _wordDragFlipped ? cursor.Start : cursor.End;
                _core.SetSelection(new TextRange(from, to));
                break;
            }
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragImageBlock >= 0)
        {
            EndImageDrag();
            _surfaceHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_isDraggingSelection)
        {
            _isDraggingSelection = false;
            _surfaceHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>position 所在词（双击）的选区；块文本空时坍缩在块首。</summary>
    private TextRange WordRangeAt(TextPosition position)
    {
        var block = _core!.Document.Blocks[position.BlockIndex];
        string text = BlockTextOps.GetPlainText(block);
        var (start, end) = WordBoundary.Expand(text, position.CharIndex);
        return new TextRange(
            new TextPosition(position.BlockIndex, start),
            new TextPosition(position.BlockIndex, end));
    }

    /// <summary>position 所在整段（三击）的选区。</summary>
    private TextRange BlockRangeAt(TextPosition position)
    {
        var block = _core!.Document.Blocks[position.BlockIndex];
        int length = BlockTextOps.GetTextLength(block);
        return new TextRange(
            new TextPosition(position.BlockIndex, 0),
            new TextPosition(position.BlockIndex, length));
    }

    /// <summary>
    /// 复选框点击：命中的 todo 块切勾选。命中判定在 Core 的
    /// <see cref="LayoutResult.HitTestTodoCheckbox"/>（与悬停共用同一判定）；
    /// 由未勾选变勾选时播放勾选动画（Phase 3 M3）。
    /// </summary>
    private bool TryToggleTodoAt(float docX, float docY)
    {
        int blockIndex = _renderer.Current?.HitTestTodoCheckbox(docX, docY) ?? -1;
        if (blockIndex < 0)
        {
            return false;
        }
        bool wasChecked = (_core!.Document.Blocks[blockIndex] as TodoBlock)?.Checked == true;
        _core.ApplyCommand(new ToggleTodoCheckedCommand(blockIndex));
        if (!wasChecked)
        {
            StartCheckAnimation(blockIndex);
        }
        return true;
    }

    /// <summary>
    /// 悬停态（Phase 3 M3/M4）：命中待办复选框或浮动图片 → 手型光标 + 对应高亮；
    /// 移出恢复 I 形。todo 命中优先于图片（复选框区域在图片外，互斥即可）。
    /// </summary>
    private void UpdateHover(float docX, float docY)
    {
        int todo = _renderer.Current?.HitTestTodoCheckbox(docX, docY) ?? -1;
        int image = todo < 0 ? HitTestImage(docX, docY) : -1;
        if (todo != _hoverTodoBlock || image != _hoverImageBlock)
        {
            _hoverTodoBlock = todo;
            _hoverImageBlock = image;
            _renderer.HoverTodoBlockIndex = todo;
            UpdateImageOverlay();
            _surface.Invalidate();
        }
        ProtectedCursor = todo >= 0 || image >= 0 ? _handCursor : _ibeamCursor;
    }

    /// <summary>启动勾选动画（再次点击打断重放；160ms 内每 16ms 重绘一帧，播完自停）。</summary>
    private void StartCheckAnimation(int blockIndex)
    {
        _animatedTodoBlock = blockIndex;
        _checkAnimStartTicks = Environment.TickCount64;
        _renderer.AnimatedTodoBlockIndex = blockIndex;
        _renderer.AnimatedTodoProgress = 0f;
        _checkAnimTimer.Start();
        _surface.Invalidate();
    }

    private void OnCheckAnimTick(object? sender, object e)
    {
        if (_animatedTodoBlock < 0)
        {
            _checkAnimTimer.Stop();
            return;
        }
        float progress = (Environment.TickCount64 - _checkAnimStartTicks) / (float)CheckAnimationMs;
        if (progress >= 1f)
        {
            _checkAnimTimer.Stop();
            _animatedTodoBlock = -1;
            _renderer.AnimatedTodoBlockIndex = -1;
            _surface.Invalidate();
            return;
        }
        _renderer.AnimatedTodoProgress = progress;
        _surface.Invalidate();
    }

    // ------------------------------------------------------------------
    // 图片交互（Phase 3 M4）：选中/悬停覆盖层、拖动改锚点、八手柄缩放、OS 拖放插图
    // ------------------------------------------------------------------

    private void BuildImageOverlay()
    {
        _imageOutline.Stroke = new SolidColorBrush(OutlineColor);
        _imageOutline.StrokeThickness = 1f;
        _imageOutline.StrokeDashArray = new DoubleCollection { 4, 3 };
        _imageOutline.IsHitTestVisible = false;
        _imageOutline.Visibility = Visibility.Collapsed;
        _imageOverlay.Children.Add(_imageOutline);

        foreach (ImageHandle handle in ImageResizeGeometry.AllHandles)
        {
            var box = new Border
            {
                Width = HandleSize,
                Height = HandleSize,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(HandleFill),
                BorderBrush = new SolidColorBrush(HandleBorderColor),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
                Tag = handle,
            };
            box.PointerPressed += OnImageHandlePressed;
            box.PointerMoved += OnImageHandleMoved;
            box.PointerReleased += OnImageHandleReleased;
            box.PointerCaptureLost += OnImageHandleCaptureLost;
            CursorShapes.SetShape(box, CursorForHandle(handle));
            _imageHandles[handle] = box;
            _imageOverlay.Children.Add(box);
        }

        _imageSizeLabelText.FontSize = 11;
        _imageSizeLabelText.Foreground = new SolidColorBrush(Colors.White);
        _imageSizeLabel.Background = new SolidColorBrush(SizeLabelBackground);
        _imageSizeLabel.CornerRadius = new CornerRadius(4);
        _imageSizeLabel.Padding = new Thickness(6, 2, 6, 2);
        _imageSizeLabel.IsHitTestVisible = false;
        _imageSizeLabel.Visibility = Visibility.Collapsed;
        _imageSizeLabel.Child = _imageSizeLabelText;
        _imageOverlay.Children.Add(_imageSizeLabel);

        // 覆盖层加在 surface 之后（z 序在上）；Canvas 无背景 → 空白处不吃指针
        _contentGrid.Children.Add(_imageOverlay);
    }

    private static Microsoft.UI.Input.InputSystemCursorShape CursorForHandle(ImageHandle handle) => handle switch
    {
        ImageHandle.TopLeft or ImageHandle.BottomRight =>
            Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
        ImageHandle.TopRight or ImageHandle.BottomLeft =>
            Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
        ImageHandle.Left or ImageHandle.Right =>
            Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        _ => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
    };

    /// <summary>命中浮动图片（返回块索引，-1 = 未命中）。</summary>
    private int HitTestImage(float docX, float docY)
    {
        var layout = _renderer.Current;
        var hit = layout?.FloatAt(docX, docY);
        if (hit is null || layout!.Blocks is not { } blocks)
        {
            return -1;
        }
        return hit.Id >= 0 && hit.Id < blocks.Count && blocks[hit.Id] is ImageBlock ? hit.Id : -1;
    }

    /// <summary>选中/取消选中图片（-1 = 取消）：选中的图片显示虚线框 + 八手柄。</summary>
    private void SelectImage(int blockIndex)
    {
        if (_selectedImageBlock == blockIndex)
        {
            return;
        }
        _selectedImageBlock = blockIndex;
        UpdateImageOverlay();
    }

    /// <summary>按当前状态刷新覆盖层几何：拖动/选中 → 框（+手柄）；仅悬停 → 细框。</summary>
    private void UpdateImageOverlay()
    {
        int block = _dragImageBlock >= 0
            ? _dragImageBlock
            : _selectedImageBlock >= 0 ? _selectedImageBlock : _hoverImageBlock;
        bool withHandles = _dragImageBlock < 0 && _selectedImageBlock >= 0;
        LayoutRect? rect = block >= 0 ? _resizePreview ?? FindFloatRect(block) : null;

        if (rect is not { } r)
        {
            _imageOutline.Visibility = Visibility.Collapsed;
            _imageSizeLabel.Visibility = Visibility.Collapsed;
            foreach (Border box in _imageHandles.Values)
            {
                box.Visibility = Visibility.Collapsed;
            }
            return;
        }

        Canvas.SetLeft(_imageOutline, r.X);
        Canvas.SetTop(_imageOutline, r.Y);
        _imageOutline.Width = Math.Max(0, r.Width);
        _imageOutline.Height = Math.Max(0, r.Height);
        _imageOutline.Visibility = Visibility.Visible;

        foreach ((ImageHandle handle, Border box) in _imageHandles)
        {
            (float cx, float cy) = ImageResizeGeometry.HandleCenter(r, handle);
            Canvas.SetLeft(box, cx - (HandleSize / 2f));
            Canvas.SetTop(box, cy - (HandleSize / 2f));
            box.Visibility = withHandles ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_resizePreview is { } preview && _resizeHandle != ImageHandle.None)
        {
            _imageSizeLabelText.Text = $"{preview.Width:0} × {preview.Height:0}";
            Canvas.SetLeft(_imageSizeLabel, preview.X + preview.Width + 8);
            Canvas.SetTop(_imageSizeLabel, preview.Y + preview.Height + 6);
            _imageSizeLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _imageSizeLabel.Visibility = Visibility.Collapsed;
        }
    }

    private LayoutRect? FindFloatRect(int blockIndex)
    {
        var floats = _renderer.Current?.Floats;
        if (floats is null)
        {
            return null;
        }
        foreach (var f in floats)
        {
            if (f.Id == blockIndex)
            {
                return f.Rect;
            }
        }
        return null;
    }

    // ---- 拖动改锚点（预览经锚点直给引擎，与提交后的结果逐像素一致）----

    private void BeginImageDrag(int blockIndex, float docX, float docY)
    {
        _dragImageBlock = blockIndex;
        _dragImageActive = false; // 先按兵不动：指针真移动了才把图片"提起来"（避免按下瞬间跳位）
        _dragPressX = docX;
        _dragPressY = docY;
        var rect = FindFloatRect(blockIndex) ?? new LayoutRect(docX, docY, 1f, 1f);
        _dragGrabOffsetX = docX - rect.X;   // 抓取点相对图片左上角的偏移：拖动跟手
        _dragGrabOffsetY = docY - rect.Y;
        _dragSizeW = rect.Width;
        _dragSizeH = rect.Height;
        _pendingAnchor = CurrentAnchorOf(blockIndex);
        _pendingSide = CurrentSideOf(blockIndex);
    }

    private FloatAnchor CurrentAnchorOf(int blockIndex)
    {
        if (_core?.Document.Blocks is { } blocks
            && blockIndex >= 0 && blockIndex < blocks.Count
            && blocks[blockIndex] is ImageBlock { Float.Anchor: { } anchor })
        {
            return anchor;
        }
        var caret = _core?.Selection.Active ?? new Core.Editing.TextPosition(0, 0);
        return new FloatAnchor(caret.BlockIndex, caret.CharIndex);
    }

    private FloatSide CurrentSideOf(int blockIndex) =>
        _core?.Document.Blocks is { } blocks
        && blockIndex >= 0 && blockIndex < blocks.Count
        && blocks[blockIndex] is ImageBlock { Float: { } placement }
            ? placement.Side
            : FloatSide.Right;

    /// <summary>
    /// 拖动预览（Phase 3 M4）：图片跟手移动——预览走 Rect 直给路径实时重排。
    /// 落点 → 锚点的换算在松手时做（见 <see cref="EndImageDrag"/>）。
    /// </summary>
    private void UpdateImageDrag(float docX, float docY)
    {
        if (_renderer.Current is null || _dragImageBlock < 0)
        {
            return;
        }
        if (!_dragImageActive)
        {
            // 拖动阈值：按下后移动超过 4dip 才算拖动（否则视为单击选中）
            float dx = docX - _dragPressX;
            float dy = docY - _dragPressY;
            if ((dx * dx) + (dy * dy) < 16f)
            {
                return;
            }
            _dragImageActive = true;
        }

        var rect = new LayoutRect(docX - _dragGrabOffsetX, docY - _dragGrabOffsetY,
            _dragSizeW, _dragSizeH);
        if (_dragFreeRect is { } old
            && Math.Abs(old.X - rect.X) < 0.5f && Math.Abs(old.Y - rect.Y) < 0.5f)
        {
            return; // 亚像素抖动不重排
        }
        _dragFreeRect = rect;
        Relayout();
    }

    /// <summary>
    /// 松手：按图片左上角在文字里的位置写回锚点（命中测试自带「向左向上找最近文字」的兜底：
    /// 预览版面上紧挨图片左侧的字，或最近的文字行），横向偏移取落点 X。
    /// 之后纵向随锚点所在行、横向保持落点位置——重排跟着这个位置走。
    /// </summary>
    private void EndImageDrag()
    {
        int block = _dragImageBlock;
        bool moved = _dragImageActive;
        var dropped = _dragFreeRect;
        _dragImageBlock = -1;
        _dragImageActive = false;
        _dragFreeRect = null;
        StopImageAutoScroll();

        if (moved && block >= 0 && _core is not null && dropped is { } rect)
        {
            // 锚点按定稿规则取（Core 的 HitTestFloatAnchor）：图片矩形纵向覆盖到的第一条文本行、
            // 图片左缘处的插入位置——"在那一行紧挨图片左侧插一个占位符"。
            // 用「去掉这张图」的自然版面：预览版面里文字已被图片挤开一截，会差一个字。
            var natural = _core.Document.GetFloats().Where(f => f.Id != block).ToArray();
            LayoutResult anchorLayout = _renderer.LayoutTransient(
                _core.Document.Blocks, natural, (float)ActualWidth);
            HitTestResult hit;
            using (anchorLayout)
            {
                hit = anchorLayout.HitTestFloatAnchor(rect);
            }
            if (hit.Found)
            {
                _pendingAnchor = new FloatAnchor(hit.BlockIndex, hit.CharIndex);
            }
            _pendingSide = rect.X + (rect.Width / 2f) < ActualWidth / 2.0
                ? FloatSide.Left
                : FloatSide.Right;
            // AnchorToChar：图片紧跟锚字符之后——横纵位置都由锚字符决定，随文字重排一起走
            _core.ApplyCommand(new MoveImageAnchorCommand(
                block, _pendingAnchor, _pendingSide, AnchorToChar: true));
            Relayout();
        }
    }

    // ---- 视口边缘自动滚动（指针贴近上下边缘时 8dip/帧）----

    private void UpdateImageAutoScroll(float viewportY)
    {
        const float edge = 20f;
        float height = (float)_scroller.ViewportHeight;
        if (viewportY < edge)
        {
            _autoScrollDelta = -8f;
            _imageScrollTimer.Start();
        }
        else if (viewportY > height - edge)
        {
            _autoScrollDelta = 8f;
            _imageScrollTimer.Start();
        }
        else
        {
            StopImageAutoScroll();
        }
    }

    private void StopImageAutoScroll() => _imageScrollTimer.Stop();

    private void OnImageAutoScrollTick(object? sender, object e)
    {
        if (_dragImageBlock < 0)
        {
            _imageScrollTimer.Stop();
            return;
        }
        _scroller.ChangeView(null, _scroller.VerticalOffset + _autoScrollDelta, null,
            disableAnimation: true);
        // 滚动改变了指针处的文档坐标：按新偏移重算预览
        UpdateImageDrag(_pointerDocX, _pointerViewportY + (float)_scroller.VerticalOffset);
    }

    // ---- 八手柄缩放（覆盖层只动预览几何，松手提交后一次重排）----

    private void OnImageHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: ImageHandle handle } box
            || FindFloatRect(_selectedImageBlock) is not { } rect)
        {
            return;
        }
        e.Handled = true;
        box.CapturePointer(e.Pointer);
        _resizeHandle = handle;
        _resizeStartRect = rect;
        _resizePreview = null;
        ClearResizeNaturalLayout();
    }

    private void OnImageHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHandle == ImageHandle.None)
        {
            return;
        }
        e.Handled = true;
        var point = e.GetCurrentPoint(_imageOverlay).Position;
        float maxWidth = Math.Max(ImageResizeGeometry.MinEdge, (float)ActualWidth);
        // 自由矩形：对边/对角固定——左/上侧手柄动左/上边缘（右下边缘不动），预览逐像素跟手。
        // 左/上侧手柄松手时还要按新左上角重锚，落位会吸到字符/行上（见 OnImageHandleReleased）。
        _resizePreview = ImageResizeGeometry.Resize(
            _resizeStartRect, _resizeHandle, (float)point.X, (float)point.Y, maxWidth);
        UpdateImageOverlay();
    }

    private void OnImageHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHandle == ImageHandle.None)
        {
            return;
        }
        e.Handled = true;
        var handle = _resizeHandle;
        _resizeHandle = ImageHandle.None;
        if (sender is Border box)
        {
            box.ReleasePointerCapture(e.Pointer);
        }
        if (_resizePreview is { } preview && _selectedImageBlock >= 0 && _core is not null)
        {
            // 左/上侧手柄把左上角挪走了 → 按新左上角重算锚点（与拖动落点同一规则），
            // 并把右下边缘钉回自由矩形的位置：图片落位由锚点决定，重锚后尺寸跟着让。
            FloatAnchor? anchor = null;
            LayoutRect committed = preview;
            if (HandleMovesTopLeft(handle)
                && NaturalLayoutForResize() is { } natural
                && ImageResizeGeometry.ResolveAnchoredResize(natural, preview, (float)ActualWidth)
                    is { } landed)
            {
                anchor = landed.Anchor;
                committed = landed.Rect;
            }
            _core.ApplyCommand(new ResizeImageCommand(_selectedImageBlock,
                MathF.Round(committed.Width), MathF.Round(committed.Height), anchor));
        }
        _resizePreview = null;
        ClearResizeNaturalLayout();
        Relayout();
    }

    private void OnImageHandleCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHandle == ImageHandle.None)
        {
            return;
        }
        _resizeHandle = ImageHandle.None;
        _resizePreview = null;
        ClearResizeNaturalLayout();
        UpdateImageOverlay();
    }

    /// <summary>左/上侧手柄：图片左上角会移动，落位需按新左上角重算锚点。</summary>
    private static bool HandleMovesTopLeft(ImageHandle handle) =>
        handle is ImageHandle.TopLeft or ImageHandle.Top or ImageHandle.TopRight
            or ImageHandle.Left or ImageHandle.BottomLeft;

    /// <summary>
    /// 重锚用的自然版面：去掉这张图（其余浮动照常参与）。用自然版面而非预览版面，
    /// 与拖动落点同一参照——预览版面里文字已被图片挤开一截，锚点会差一个字。手势内复用。
    /// </summary>
    private LayoutResult? NaturalLayoutForResize()
    {
        if (_resizeNaturalLayout is not null || _core is null || _selectedImageBlock < 0)
        {
            return _resizeNaturalLayout;
        }
        var floats = _core.Document.GetFloats()
            .Where(f => f.Id != _selectedImageBlock)
            .ToArray();
        _resizeNaturalLayout = _renderer.LayoutTransient(
            _core.Document.Blocks, floats, (float)ActualWidth);
        return _resizeNaturalLayout;
    }

    private void ClearResizeNaturalLayout()
    {
        _resizeNaturalLayout?.Dispose();
        _resizeNaturalLayout = null;
    }

    // ---- OS 拖放插图（文件/位图拖入 → 落点锚定）----

    private void OnDragOverHandler(object sender, DragEventArgs e)
    {
        bool accepted = e.DataView.Contains(StandardDataFormats.Bitmap)
            || e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (!accepted || _renderer.Current is null)
        {
            return;
        }

        var point = e.GetPosition(_surfaceHost);
        var hit = _renderer.Current.HitTest(
            (float)point.X, (float)(point.Y + _scroller.VerticalOffset));
        _dropIndicator = hit.Found ? hit.CaretPosition : null;
        _surface.Invalidate();
    }

    private void OnDragLeaveHandler(object sender, DragEventArgs e)
    {
        _dropIndicator = null;
        _surface.Invalidate();
    }

    private async void OnDropHandler(object sender, DragEventArgs e)
    {
        _dropIndicator = null;
        try
        {
            // 落点 → 光标位置：InsertImageCommand 按当前选区字符锚定图片
            var point = e.GetPosition(_surfaceHost);
            var hit = _renderer.Current?.HitTest(
                (float)point.X, (float)(point.Y + _scroller.VerticalOffset));
            if (hit is { Found: true } found && _core is not null)
            {
                _core.SetSelection(Core.Editing.TextRange.Collapse(found.CaretPosition));
            }

            if (e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await e.DataView.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                if (_core is not null)
                {
                    await WinClipboard.InsertImageAsync(_core, stream);
                }
                return;
            }
            if (e.DataView.Contains(StandardDataFormats.StorageItems) && _core is not null)
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && WinClipboard.IsImageFile(file))
                    {
                        using var stream = await file.OpenReadAsync();
                        await WinClipboard.InsertImageAsync(_core, stream);
                        break; // 一次拖放插一张（多文件时取第一张图片）
                    }
                }
            }
        }
        catch
        {
            // 拖放内容解码失败：静默忽略（与剪贴板粘贴路径同样宽松）
        }
        finally
        {
            _surface.Invalidate();
        }
    }

    private void OnRenderViewport(CanvasDrawingSession session, Windows.Foundation.Rect viewport, float scale)
    {
        if (_renderer.Current is null)
        {
            return;
        }

        // 选区高亮（文字下层，§4.5）→ 文字底色（Phase 3 打磨）→ 浮动/文字
        if (_core is not null && !_core.Selection.IsCollapsed)
        {
            var rects = CaretGeometryCalculator.GetSelectionRects(_renderer.Current, _core.Selection);
            foreach (var r in rects)
            {
                session.FillRectangle(new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height), SelectionFill);
            }
        }

        _renderer.RenderTextBackgrounds(session, _renderer.Current, viewport);
        _renderer.Render(session, _renderer.Current, viewport, InkColor, DebugOverlay,
            includeTextBackgrounds: false);

        // OS 拖放落点指示线（Phase 3 M4）：拖入悬停期间画在落点字符处
        if (_dropIndicator is { } drop)
        {
            var dropCaret = CaretGeometryCalculator.GetCaret(_renderer.Current, drop);
            if (dropCaret is { } dc)
            {
                session.DrawLine(dc.X, dc.Y, dc.X, dc.Y + dc.Height, InkColor, 2f);
            }
        }

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
