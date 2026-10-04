using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Controls;
using LumiMemo.WinUI.Services;
using LumiMemo.WinUI.ViewModels;
using LumiText.WinUI.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace LumiMemo.WinUI;

/// <summary>一张便签的窗口：只管窗口本身（AppWindow、玻璃、标题栏、关闭协议），业务状态在 ViewModel 里。</summary>
/// <remarks>
/// 之前的 418 行 code-behind（保存编排、标题订阅、状态文案）已经搬进
/// <see cref="NoteViewModel"/>；这里剩下的每一条都与「窗口」直接相关。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly NoteViewModel _viewModel;
    private readonly NoteLayout _layout;
    private readonly ILayoutStore _layoutStore;
    private readonly Action<Guid> _onClosed;
    private readonly INoteWindowActions _actions;
    private readonly LumiEditor _editor;
    private readonly AppWindow _appWindow;
    private AcrylicBackdrop? _backdrop;
    private bool _isApplicationExiting;
    private bool _closeApproved;
    private bool _closeInProgress;

    /// <summary>当前文字底色：按钮本体的颜色、色带滑块的起点、左键点击时的落点。
    /// 初值取便签黄纸色（与旧的色板默认一致）。</summary>
    private Windows.UI.Color _highlightColor = NoteColorPalette.Paper(NoteColor.Yellow);

    /// <summary>工具栏底色按钮的悬停提示（与 MainWindow.xaml 里那串字保持一致）：
    /// 展开色带期间要摘掉——它悬在按钮上方，会挡住刚铺开的色带。</summary>
    private const string HighlightButtonTip = "文字底色（左键＝用当前色刷选中文字；右键＝展开色带取色）";

    public MainWindow(
        NoteViewModel viewModel,
        NoteLayout layout,
        ILayoutStore layoutStore,
        Action<Guid> onClosed,
        INoteWindowActions actions)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(onClosed);
        ArgumentNullException.ThrowIfNull(actions);

        InitializeComponent();
        _appWindow = AppWindow;

        _viewModel = viewModel;
        _layout = layout;
        _layoutStore = layoutStore;
        _onClosed = onClosed;
        _actions = actions;

        // 正文左右内边距 12（旧内核时代的便签是 18/16 四边，这里调窄了些，只做左右）：
        // 文字不贴窗口边，右边也给滚动条让出位置
        _editor = new LumiEditor { HostWindow = this, ContentInset = 12 };
        _editor.CaretBlockChanged += OnCaretBlockChanged;
        EditorHost.Children.Add(_editor);
        _viewModel.AttachDocument(new LumiEditorDocument(_editor));
        _viewModel.TopMostChanged += OnTopMostChanged;

        Title = "LumiMemo";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        ConfigureWindow();
        _backdrop = AcrylicBackdrop.Apply(this, Root);

        // 文字底色：工具栏的圆角方块按钮 ↔ 展开式色带取色器（色带本体在按钮栏里，接给取色器驱动）
        HighlightPickerLayer.AttachStrip(HighlightStrip, HighlightStripSpacer);
        HighlightPickerLayer.ColorPicked += (_, color) => ApplyHighlight(color);
        HighlightPickerLayer.Closed += (_, _) =>
        {
            HighlightButtonFill.Visibility = Visibility.Visible;
            SetHighlightButtonIdle(false);
        };
        UpdateHighlightButtonFill();
        // 点到别处＝取消取色。编辑器会把 PointerPressed 标成 Handled，所以得用 handledEventsToo 挂上
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);

        EditorHost.Loaded += OnEditorHostLoaded;
        _appWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        // 进度圈出现/消失会改变标题可用的宽度（生成中给它 22px，平时全给标题）。
        TitleProgress.SizeChanged += OnTitleProgressSizeChanged;
    }

    /// <summary>XAML 的 x:Bind 从这里取值。</summary>
    public NoteViewModel ViewModel => _viewModel;

    /// <summary>标题区随标题栏尺寸重算（见 <see cref="UpdateTitleLayout"/>）。</summary>
    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleLayout();

    private void OnTitleProgressSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleLayout();

    /// <summary>
    /// 标题区布局：下层文字按自然宽度铺（不按字裁剪），上层遮罩（省略号/进度圈/刷新按钮）
    /// 的位置 = min(文字自然宽, 限位)——随窗口宽度连续变化，拖动时不按字符跳变。
    /// </summary>
    /// <remarks>
    /// 装不下时遮罩带着省略号整块顶到限位（刷新按钮右缘与图标组同距 3px），
    /// 文字裁在遮罩左缘、由省略号自然盖住；装得下时遮罩紧贴文字右缘。
    /// </remarks>
    private void UpdateTitleLayout()
    {
        // 工具按钮恒为四个（最小窗口宽度保证放得下，见 MinWindowWidth）。
        double column = Math.Max(
            0, TitleBarGrid.ActualWidth - 21 /* 左右内边距 14 + 7 */ - ToolsPanel.ActualWidth);

        double overlayWidth = RefreshTitleButton.ActualWidth
            + RefreshTitleButton.Margin.Left
            + RefreshTitleButton.Margin.Right;

        if (TitleProgress.Visibility == Visibility.Visible)
        {
            overlayWidth += TitleProgress.ActualWidth + TitleProgress.Margin.Left;
        }

        // 量一次文字的自然宽度（不限宽）：遮罩贴它，也用它判断装不装得下。
        TitleText.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        double natural = TitleText.DesiredSize.Width;

        bool truncated = natural + overlayWidth > column;
        double overlayX;

        if (truncated)
        {
            // 遮罩整块顶到限位；文字在遮罩左缘前"渐隐"收尾（比省略号优雅）——
            // 用文字前景色的渐变画刷实现（WinUI 3 没有 OpacityMask），
            // 渐隐之后再叠一道裁剪兜底，保证遮罩附近没有半截字漏出来。
            overlayX = Math.Max(0, column - overlayWidth);
            TitleText.Foreground = BuildFadeForeground(overlayX, natural);
            TitleText.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, overlayX, TitleBarGrid.ActualHeight),
            };
        }
        else
        {
            overlayX = natural;
            TitleText.Foreground = TitleSolidForeground;
            TitleText.Clip = null;
        }

        TitleOverlay.Margin = new Thickness(overlayX, 0, 0, 0);
    }

    private static readonly SolidColorBrush TitleSolidForeground =
        new(Windows.UI.Color.FromArgb(255, 0x40, 0x37, 0x47));

    /// <summary>标题渐隐前景：从 <paramref name="edgeX"/> 往左约 36px 内把文字淡出到全透明。</summary>
    private static LinearGradientBrush BuildFadeForeground(double edgeX, double natural)
    {
        double fade = Math.Min(36, edgeX);
        double end = natural > 0 ? edgeX / natural : 0;
        double start = natural > 0 ? (edgeX - fade) / natural : 0;

        Windows.UI.Color solid = Windows.UI.Color.FromArgb(255, 0x40, 0x37, 0x47);
        Windows.UI.Color clear = Windows.UI.Color.FromArgb(0, 0x40, 0x37, 0x47);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };

        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = solid });

        if (start > 0)
        {
            brush.GradientStops.Add(new GradientStop { Offset = start, Color = solid });
        }

        brush.GradientStops.Add(new GradientStop { Offset = end, Color = clear });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = clear });

        return brush;
    }

    /// <summary>x:Bind 的函数绑定不能直接产 Visibility，借这个转换（生成代码按实例调用）。</summary>
    private Visibility ToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private async void OnEditorHostLoaded(object sender, RoutedEventArgs args)
    {
        EditorHost.Loaded -= OnEditorHostLoaded;
        try
        {
            await _editor.LoadAsync(_viewModel.Note.RichTextContent);
        }
        catch (Exception exception)
        {
            _viewModel.ShowHint($"读取便笺失败：{exception.Message}");
        }
    }

    private void ConfigureWindow()
    {
        int width = Math.Max(320, (int)Math.Round(_layout.Width));
        int height = Math.Max(300, (int)Math.Round(_layout.Height));
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        bool hasSavedPosition = _layout.X != 0 || _layout.Y != 0;
        AppWindow.Move(hasSavedPosition
            ? new PointInt32((int)Math.Round(_layout.X), (int)Math.Round(_layout.Y))
            : new PointInt32(workArea.X + workArea.Width - width - 40, workArea.Y + 80));
        AppWindow.IsShownInSwitchers = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = _layout.IsTopMost;
        }

        AppWindow.Changed += OnAppWindowChanged;
    }

    private void OnTopMostChanged(bool isTopMost)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = isTopMost;
        }
    }

    // ---- 编辑命令转发（命令属于编辑区，窗口只做转手） ----

    private async void OnNewNoteClick(object sender, RoutedEventArgs e) =>
        await _actions.CreateNoteAsync(_viewModel.Note);

    private async void OnDeleteNoteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await _actions.DeleteNoteAsync(_viewModel.Note))
            {
                // false 的含义是「存不下来」——窗口还开着，提示用户先处理保存失败。
                await ShowDialogAsync("未删除", "便签有修改尚未保存成功，已保留原地。请稍后再试。");
            }
        }
        catch (Exception exception)
        {
            // 入回收站失败时本窗口已被管理器关掉：没地方弹提示，只能记日志。
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private void OnRefreshTitleClick(object sender, RoutedEventArgs e) => _viewModel.RegenerateTitle();

    private async Task ShowDialogAsync(string title, string content) =>
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "确定",
        }.ShowAsync();

    private void OnBoldClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("bold");

    private void OnItalicClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("italic");

    private void OnUnderlineClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("underline");

    private void OnStrikeClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("strikethrough");

    private void OnBulletClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("bullet");

    private void OnTodoClick(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("todo");

    private void OnHeading1Click(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("h1");

    private void OnHeading2Click(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("h2");

    private void OnHeading3Click(object sender, RoutedEventArgs e) => _editor.ExecuteCommand("h3");

    /// <summary>标题按钮态随光标所在块刷新（Phase 3 M2 §6.1）：已在该级别显示为按下；
    /// 分点/勾选按钮同源（Phase 3 打磨）：光标所在块已带标记就显示为激活。</summary>
    private void OnCaretBlockChanged(object? sender, EventArgs e)
    {
        int level = _editor.CaretHeadingLevel;
        Heading1Button.IsChecked = level == 1;
        Heading2Button.IsChecked = level == 2;
        Heading3Button.IsChecked = level == 3;
        BulletButton.IsChecked = _editor.CaretIsBullet;
        TodoButton.IsChecked = _editor.CaretIsTodo;
    }

    /// <summary>
    /// 文字底色：工具栏上那个圆角方块**就是**按钮本体，底色＝当前色。
    /// 左键＝把当前色刷到选中文字；右键＝展开色带取色（见 <see cref="HighlightPicker"/>）。
    /// </summary>
    /// <remarks>
    /// 色值给实色：RTF 颜色表没有 alpha 通道，带透明度的底色在复制粘贴时会被抹平
    /// （高亮面积小，实色不影响毛玻璃观感）。
    /// 没选中文字时 <c>SetInlineBackground</c> 自己会忽略——这时只更新"当前色"（按钮换色），不报错。
    /// </remarks>
    private void OnHighlightClick(object sender, RoutedEventArgs e) => ApplyHighlight(_highlightColor);

    /// <summary>右键：色带在按钮栏里向右展开（右侧按钮被推开），方块缩小、滑到当前色的位置上当滑块。</summary>
    private void OnHighlightRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (HighlightPickerLayer.IsOpen)
        {
            return;
        }

        // 先量方块的矩形，再把它藏起来——Collapsed 的元素量不到有效坐标（这是上一版动画失效的根因）
        var squareRect = HighlightButtonFill.TransformToVisual(Root)
            .TransformBounds(new Rect(0, 0, HighlightButtonFill.ActualWidth, HighlightButtonFill.ActualHeight));
        HighlightButtonFill.Visibility = Visibility.Collapsed; // 方块交给滑块接管：视觉上就是它滑过去了
        SetHighlightButtonIdle(true);
        HighlightPickerLayer.Open(squareRect, _highlightColor);
        e.Handled = true;
    }

    /// <summary>色带松手 / 左键点击的落点：记住当前色、按钮换色、刷到选中文字。</summary>
    private void ApplyHighlight(Windows.UI.Color color)
    {
        _highlightColor = color;
        UpdateHighlightButtonFill();
        _editor.SetInlineBackground(
            new LumiText.Core.Documents.Color32(0xFF, color.R, color.G, color.B));
    }

    private void UpdateHighlightButtonFill() =>
        HighlightButtonFill.Background = new SolidColorBrush(_highlightColor);

    /// <summary>
    /// 展开期间把原按钮"熄灭"：悬停/按下的底色与边框改成透明，并摘掉悬停提示与焦点框。
    /// 光设 <c>IsHitTestVisible = false</c> 不够——指针**已经**在按钮上，PointerOver 是**粘住的**
    /// （元素不再收输入，也就等不到 PtrExited 来复位），那圈边框会一直画在色带上面（截图里那个圆角空框）。
    /// 所以把模板取的那几个画刷在元素级覆盖成透明，状态还在也不显形。
    /// 顺带把**焦点视觉**也关掉：WinUI 的 FocusVisual 同样是"外扩一圈圆角框"，Tab 到这里也会露出来。
    /// </summary>
    private void SetHighlightButtonIdle(bool idle)
    {
        if (idle)
        {
            var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            HighlightButton.Resources["ButtonBackgroundPointerOver"] = transparent;
            HighlightButton.Resources["ButtonBackgroundPressed"] = transparent;
            HighlightButton.Resources["ButtonBorderBrushPointerOver"] = transparent;
            HighlightButton.Resources["ButtonBorderBrushPressed"] = transparent;
            HighlightButton.Resources["FocusVisualPrimaryBrush"] = transparent;
            HighlightButton.Resources["FocusVisualSecondaryBrush"] = transparent;
            HighlightButton.IsHitTestVisible = false;
            HighlightButton.UseSystemFocusVisuals = false;
            VisualStateManager.GoToState(HighlightButton, "Normal", false);
            ToolTipService.SetToolTip(HighlightButton, null);
        }
        else
        {
            HighlightButton.Resources.Remove("ButtonBackgroundPointerOver");
            HighlightButton.Resources.Remove("ButtonBackgroundPressed");
            HighlightButton.Resources.Remove("ButtonBorderBrushPointerOver");
            HighlightButton.Resources.Remove("ButtonBorderBrushPressed");
            HighlightButton.Resources.Remove("FocusVisualPrimaryBrush");
            HighlightButton.Resources.Remove("FocusVisualSecondaryBrush");
            HighlightButton.IsHitTestVisible = true;
            HighlightButton.UseSystemFocusVisuals = true;
            ToolTipService.SetToolTip(HighlightButton, HighlightButtonTip);
        }
    }

    /// <summary>点亮别处＝取消取色（色带收起，不套用）。</summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HighlightPickerLayer.IsOpen
            || IsWithin(e.OriginalSource as DependencyObject, HighlightPickerLayer))
        {
            return;
        }
        HighlightPickerLayer.Close(apply: false);
    }

    /// <summary>Esc 也取消（预览键隧道到根，先于编辑器的 Esc 处理）。</summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HighlightPickerLayer.IsOpen && e.Key == Windows.System.VirtualKey.Escape)
        {
            HighlightPickerLayer.Close(apply: false);
            e.Handled = true;
        }
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ---- 关闭与退出 ----

    private void OnCloseClick(object sender, RoutedEventArgs e) => _ = TryCloseAsync();

    public void ShowFromTray()
    {
        _layout.IsOpen = true;
        _layoutStore.MarkDirty();
        AppWindow.Show();
        Activate();
    }

    public void HideWindow() => AppWindow.Hide();

    /// <summary>删除流程的关窗：先把最新内容落盘再关；保存失败留在原地并返回 false。</summary>
    /// <remarks>
    /// 与普通关闭的区别：删除是一条不可逆的动作链（文件马上要进回收站），
    /// 保存失败时必须停在窗口里让用户看见，不能默默把旧内容删掉。
    /// </remarks>
    public async Task<bool> PersistAndCloseForDeleteAsync()
    {
        if (!await _viewModel.TryPersistOnCloseAsync())
        {
            return false;
        }

        _closeApproved = true;
        CaptureLayout();
        Close();

        return true;
    }

    /// <summary>托盘退出路径：保存尽力而为，无论成败都关——「退出总会发生」。</summary>
    public async Task CloseForExitAsync()    {
        _isApplicationExiting = true;

        try
        {
            await _viewModel.TryPersistOnCloseAsync();
        }
        catch (Exception)
        {
            // TryPersistOnCloseAsync 契约上不抛；真抛了也只说明这次保存没成，退出照走。
        }

        _closeApproved = true;
        CaptureLayout();
        Close();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            CaptureLayout();
            return;
        }

        if (!_viewModel.HasPendingSave)
        {
            _closeApproved = true;
            CaptureLayout();
            return;
        }

        // 有没落盘的内容：先取消这次关闭，等异步保存成功后再关；
        // 保存失败则留在窗口里，状态条已显示「保存失败」。
        args.Cancel = true;
        _ = TryCloseAsync();
    }

    private async Task TryCloseAsync()
    {
        if (_closeInProgress || _closeApproved)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            if (await _viewModel.TryPersistOnCloseAsync())
            {
                _closeApproved = true;
                CaptureLayout();
                Close();
            }
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 先做同步清理：窗口管理器要立刻把这个实例摘掉（否则同一张便签重开拿不到新窗口）。
        _viewModel.TopMostChanged -= OnTopMostChanged;
        Closed -= OnWindowClosed;
        _onClosed(_viewModel.Id);
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Closing -= OnWindowClosing;
        EditorHost.Loaded -= OnEditorHostLoaded;

        if (!_isApplicationExiting)
        {
            _layout.IsOpen = false;
        }

        _layoutStore.MarkDirty();

        // XAML 对象的拆解必须在同步段做完：拖到 await 之后就可能在别的窗口关闭的
        // 嵌套消息泵里执行，此时 XAML 协同层正在收摊——退出时 0xC000027B 的温床。
        try
        {
            _editor.Dispose();
            _viewModel.Dispose();
            _backdrop?.Dispose();
            _backdrop = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }

        try
        {
            await _layoutStore.FlushAsync();
        }
        catch (Exception)
        {
            // 关闭必须可用，即使设备状态文件写不进去。
        }
    }

    /// <summary>
    /// 最小窗口宽度：再窄就放不下四个操作按钮 + 刷新按钮（21 内边距 + 137 工具 + 35 刷新 + 余量）。
    /// 拖到下限就顶住，四个按钮因此永远都在。
    /// </summary>
    private const int MinWindowWidth = 200;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        if (args.DidSizeChange && AppWindow.Size.Width < MinWindowWidth)
        {
            // 顶回最小宽度；这次 Resize 会再触发一次 Changed，布局在那里被捕获。
            AppWindow.Resize(new SizeInt32(MinWindowWidth, AppWindow.Size.Height));
            return;
        }

        CaptureLayout();
        _layoutStore.MarkDirty();
    }

    private void CaptureLayout()
    {
        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        _layout.X = position.X;
        _layout.Y = position.Y;
        _layout.Width = size.Width;
        _layout.Height = size.Height;
        _layout.ExpandedHeight = size.Height;
    }

    /// <summary>
    /// <see cref="LumiEditor"/> → <see cref="IRichTextDocument"/> 的薄适配器。
    /// LumiText.WinUI 不依赖 LumiMemo.WinUI（接口所在程序集），避免循环依赖，
    /// 故接口实现放在主程序侧；成员签名一一对应，纯转发。
    /// </summary>
    private sealed class LumiEditorDocument(LumiEditor editor) : IRichTextDocument
    {
        public string PlainText => editor.PlainText;
        public event EventHandler? UserEdited
        {
            add => editor.UserEdited += value;
            remove => editor.UserEdited -= value;
        }
        public byte[] SaveContent() => editor.SaveContent();
        public Task LoadAsync(byte[] content) => editor.LoadAsync(content);
    }
}
