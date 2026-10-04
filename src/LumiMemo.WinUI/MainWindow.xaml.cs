using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    /// <summary>工具栏那两个「当前值」（文字底色的颜色、常用标题级别）：全局一份、随设置落盘。</summary>
    private readonly ToolbarPreferences _toolbar;

    /// <summary>工具栏底色按钮的悬停提示（与 MainWindow.xaml 里那串字保持一致）：
    /// 展开色带期间要摘掉——它悬在按钮上方，会挡住刚铺开的色带。</summary>
    private const string HighlightButtonTip = "文字底色（左键＝用当前色刷选中文字；右键＝展开色带取色）";

    public MainWindow(
        NoteViewModel viewModel,
        NoteLayout layout,
        ILayoutStore layoutStore,
        Action<Guid> onClosed,
        INoteWindowActions actions,
        ToolbarPreferences toolbarPreferences)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(onClosed);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(toolbarPreferences);

        InitializeComponent();
        _appWindow = AppWindow;

        _viewModel = viewModel;
        _layout = layout;
        _layoutStore = layoutStore;
        _onClosed = onClosed;
        _actions = actions;
        _toolbar = toolbarPreferences;

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

        // 底色与常用标题级别都是**全局**值：别的便签窗口改了，这里也要跟上
        _toolbar.HighlightColorChanged += OnToolbarHighlightColorChanged;
        _toolbar.HeadingLevelChanged += OnToolbarHeadingLevelChanged;
        _headingButtonCorners = HeadingButton.CornerRadius; // 样式里的值，收起态要恢复成它
        _headingBadgeRestingScale = HeadingFaceLevelSlot.FontSize / HeadingBadgeText.FontSize;

        // 角标是浮层上的元素，位置得自己算：每次布局完校一次——它要跟着 H 一起被推走
        // （底色色带展开时整组会右移），展开期间还要停在选中的那个格子上。
        HeadingBadgeLayer.LayoutUpdated += (_, _) => SyncHeadingBadge();
        UpdateHighlightButtonFill();
        UpdateHeadingVisuals();
        // 右下角保存状态：VM 报一次就换一次图标（StatusText 连带字数、标题一起报）
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateStatusVisuals();
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

    // ---- 标题级别：一个 "H" 按钮 + 向右展开的 1/2/3 选项（2026-10-04 合并）----

    /// <summary>
    /// 选项帘子展开后的宽度＝三个格子的宽 + 两道格距。
    /// </summary>
    /// <remarks>
    /// <b>别去问那条 StackPanel 的 <c>DesiredSize</c></b>：帘子收起时外框宽 0，而没有显式宽度的元素，
    /// 它的 DesiredSize 会被**夹到可见宽度**上——那条 StackPanel 量出来就是 0，于是"展开"是把宽度
    /// 从 0 动到 0：格子根本没出来，而角标照旧飞到格子的位置上（它的落点算的是格子自己的宽，
    /// 格子有显式 <c>Width</c>、量出来是准的）——看上去就是"数字孤零零飘在那儿"。
    /// 所以这里按格子逐个加：改格子宽度或格距（XAML 里那三个 `Width` 与 `HeadingChoiceStrip.Spacing`）
    /// 不用回来改代码。
    /// </remarks>
    private double HeadingChoiceWidth =>
        ChoiceWidth(HeadingChoice1) + ChoiceWidth(HeadingChoice2) + ChoiceWidth(HeadingChoice3)
        + (HeadingChoiceStrip.Spacing * 2);

    /// <summary>
    /// 一个格子的宽度：优先取**显式写死的** <c>Width</c>（那个数不会被布局夹），没写才退回实测宽度。
    /// 帘子收起时格子排在 0 宽的槽里，实测值有被夹到 0 的风险——显式值没这个问题。
    /// </summary>
    private static double ChoiceWidth(FrameworkElement chip) =>
        double.IsNaN(chip.Width) ? chip.DesiredSize.Width : chip.Width;

    private const int HeadingExpandMs = 250;   // ≈ ControlNormalAnimationDuration
    private const int HeadingCollapseMs = 167; // ≈ ControlFastAnimationDuration

    /// <summary>选项帘子是否展开（宽度做动画期间读不出准确值，所以单独记一个态）。</summary>
    private bool _headingOpen;

    /// <summary>正在跑的展开/收起动画：Storyboard 播完会**保持**动画值、盖住之后的直接赋值，所以要记账。</summary>
    private Storyboard? _headingStory;

    /// <summary>H 按钮在样式里的角半径（收起态用；展开时右边两角要改成直角，见 <see cref="OpenHeadingChoices"/>）。</summary>
    private readonly CornerRadius _headingButtonCorners;

    /// <summary>角标停在收起位（H 的下角标）时的缩放：与面里那个占位数字的字号对齐（两个字号都来自 XAML）。</summary>
    private readonly double _headingBadgeRestingScale;

    /// <summary>角标正被动画驱动：既别让 <see cref="SyncHeadingBadge"/> 改写它的位置，也别换它的数字。</summary>
    private bool _headingBadgeAnimating;

    /// <summary>上次把角标摆到哪儿了（没变就不写变换，免得每次布局都白重画一遍）。</summary>
    private (double X, double Y, double Scale) _headingBadgePlaced = (double.NaN, double.NaN, double.NaN);

    /// <summary>右键点 H：1/2/3 在它右边向右展开（H 自己不动）。</summary>
    private void OnHeadingRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        OpenHeadingChoices();
    }

    /// <summary>
    /// 左键点 H 本体：把**常用级别**套用到光标所在块——与合并之前那三个按钮的逻辑完全一样
    /// （命令自带 toggle 语义：已经在这一档就回正文）。展开期间点它＝收起。
    /// </summary>
    private void OnHeadingClick(object sender, RoutedEventArgs e)
    {
        if (_headingOpen)
        {
            CollapseHeadingChoices();
            return;
        }

        _editor.ExecuteCommand($"h{_toolbar.HeadingLevel}");
    }

    /// <summary>
    /// 左键点选项：把它设为**常用级别**（全局保存，下次打开/新建便签还是它），然后收起。
    /// 不动正文——套用是左键点 H 那一下的事，这里只管"平时用几级"。
    /// </summary>
    private void OnHeadingChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string level }
            && int.TryParse(level, out int parsed))
        {
            _toolbar.SetHeadingLevel(parsed);
        }
        CollapseHeadingChoices();
    }

    /// <summary>帘子宽度变了就同步裁剪框（WinUI 没有 ClipToBounds，没有它子元素会溢出 0 宽的外框）。</summary>
    private void OnHeadingChoiceHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeadingChoiceClip();

    /// <summary>动画期间逐帧对齐裁剪框——不指望 SizeChanged 一定会跟着布局动画走。</summary>
    private void OnHeadingHostLayoutUpdated(object? sender, object e) => UpdateHeadingChoiceClip();

    /// <summary>开始逐帧跟裁剪；半途换场时先摘再挂，避免同一个处理器挂重。</summary>
    private void StartTrackingHeadingClip()
    {
        HeadingChoiceHost.LayoutUpdated -= OnHeadingHostLayoutUpdated;
        HeadingChoiceHost.LayoutUpdated += OnHeadingHostLayoutUpdated;
    }

    private void StopTrackingHeadingClip()
    {
        HeadingChoiceHost.LayoutUpdated -= OnHeadingHostLayoutUpdated;
        UpdateHeadingChoiceClip();
    }

    private void UpdateHeadingChoiceClip() =>
        HeadingChoiceClip.Rect = new Rect(0, 0, HeadingChoiceHost.ActualWidth, HeadingChoiceHost.ActualHeight);

    /// <summary>
    /// 展开：帘子宽 0 → 42，同时角标从 H 的下角标位置**飞到当前选中的那个选项上**
    /// （选中项自己的数字让位，看起来就是角标挪了过去）。
    /// </summary>
    private void OpenHeadingChoices()
    {
        if (_headingOpen)
        {
            return;
        }
        _headingOpen = true;
        UpdateHeadingVisuals();  // 起飞前先对齐（角标的数字此刻就是当前级别）
        _headingBadgeAnimating = true;

        // 与第一个选项贴死的那条边改成直角（左边两角保持圆角）——H 与 1/2/3 连成一条"连体条"
        HeadingButton.CornerRadius = new CornerRadius(
            _headingButtonCorners.TopLeft, 0, 0, _headingButtonCorners.BottomLeft);

        SyncHeadingChoiceDigits(); // 选中那格的数字让位给要飞过来的角标

        // 基准值先设成终态、动画只负责过程：某条没跑成也不会留下坏状态
        // （同一帧里 Begin() 就挂上了动画，首帧读到的仍是 From，不会闪终态）
        HeadingChoiceHost.Width = HeadingChoiceWidth;

        // 宽度写成终态之后**强制排一次版**，再去量角标要落的那一格：量的是它**真正待着**的位置，
        // 而不是按"格宽 + 间距"推算出来的——格子怎么调（宽窄/间距/外边距）都不会偏。
        HeadingChoiceHost.UpdateLayout();
        Point badgeTarget = HeadingChoiceCenter(_toolbar.HeadingLevel);

        StartTrackingHeadingClip();

        var story = new Storyboard();
        AddHeadingAnimation(story, HeadingChoiceHost, "Width", 0d, HeadingChoiceWidth, HeadingExpandMs, isLayout: true);
        AddHeadingBadgeAnimations(story, badgeTarget, 1d, HeadingExpandMs);
        RunHeadingAnimation(story, () =>
        {
            HeadingChoiceHost.Width = HeadingChoiceWidth;
            PlaceHeadingBadge(badgeTarget, 1d);
            _headingBadgeAnimating = false;
            StopTrackingHeadingClip();
        });
    }

    /// <summary>
    /// 收起：帘子宽 → 0，角标从选项上**飞回 H 的下角标位置**。
    /// 角标带着**旧**数字飞（落地那一下才换新的）——否则它会顶着新数字停在旧格子上，像穿帮。
    /// </summary>
    private void CollapseHeadingChoices()
    {
        if (!_headingOpen)
        {
            return;
        }
        _headingOpen = false;
        _headingBadgeAnimating = true; // 先锁住角标，再刷新界面（刷新会想要换它的数字）
        UpdateHeadingVisuals();
        StartTrackingHeadingClip();

        Point badgeTarget = HeadingFaceLevelSlotCenter();

        var story = new Storyboard();
        // From 取"当前值"：半途打断也能从眼下这一帧接着收
        AddHeadingAnimation(story, HeadingChoiceHost, "Width", HeadingChoiceHost.Width, 0d, HeadingCollapseMs, isLayout: true);
        AddHeadingBadgeAnimations(story, badgeTarget, _headingBadgeRestingScale, HeadingCollapseMs);
        RunHeadingAnimation(story, () =>
        {
            HeadingChoiceHost.Width = 0d;
            HeadingButton.CornerRadius = _headingButtonCorners; // 选项没了，H 恢复成一个独立的圆角按钮
            PlaceHeadingBadge(badgeTarget, _headingBadgeRestingScale);
            _headingBadgeAnimating = false;
            UpdateHeadingBadgeText();          // 到家了才换新数字
            ShowAllHeadingChoiceDigits();      // 选项的数字各自归位
            StopTrackingHeadingClip();
        });
    }

    // ---- 角标：H 的下角标 ↔ 飞过去当选项的数字 ----

    private void AddHeadingBadgeAnimations(Storyboard story, Point target, double scale, int milliseconds)
    {
        Point half = HeadingBadgeHalf;

        // 曲线用对称的缓入缓出：角标要横跨小半个工具栏，缓出（起步就冲出去）看着像"飞过头"。
        AddHeadingAnimation(story, HeadingBadgeShift, "X", HeadingBadgeShift.X, target.X - half.X, milliseconds, isLayout: false, EasingMode.EaseInOut);
        AddHeadingAnimation(story, HeadingBadgeShift, "Y", HeadingBadgeShift.Y, target.Y - half.Y, milliseconds, isLayout: false, EasingMode.EaseInOut);
        AddHeadingAnimation(story, HeadingBadgeScale, "ScaleX", HeadingBadgeScale.ScaleX, scale, milliseconds, isLayout: false, EasingMode.EaseInOut);
        AddHeadingAnimation(story, HeadingBadgeScale, "ScaleY", HeadingBadgeScale.ScaleY, scale, milliseconds, isLayout: false, EasingMode.EaseInOut);
    }

    /// <summary>角标那个框的半宽半高（＝选项格子的一半）：把"框心"对到目标中心就靠它。</summary>
    private Point HeadingBadgeHalf => new(HeadingBadge.Width / 2, HeadingBadge.Height / 2);

    /// <summary>把角标摆到某个中心（<paramref name="scale"/> = 1 是选项的字号，收起时按占位的字号缩小）。</summary>
    private void PlaceHeadingBadge(Point center, double scale)
    {
        Point half = HeadingBadgeHalf;
        double x = center.X - half.X;
        double y = center.Y - half.Y;
        if (_headingBadgePlaced == (x, y, scale))
        {
            return;
        }

        HeadingBadgeShift.X = x;
        HeadingBadgeShift.Y = y;
        HeadingBadgeScale.ScaleX = scale;
        HeadingBadgeScale.ScaleY = scale;
        _headingBadgePlaced = (x, y, scale);

        // 量到位置之后才露出来（否则头一帧它会闪在工具栏左上角）
        HeadingBadge.Opacity = 1;
    }

    /// <summary>每次布局完校一次角标与选项数字——位置/选中项变了就自己跟上。</summary>
    private void SyncHeadingBadge()
    {
        if (_headingBadgeAnimating)
        {
            return;
        }

        if (_headingOpen)
        {
            SyncHeadingChoiceDigits();
            PlaceHeadingBadge(HeadingChoiceCenter(_toolbar.HeadingLevel), 1d);
        }
        else
        {
            PlaceHeadingBadge(HeadingFaceLevelSlotCenter(), _headingBadgeRestingScale);
        }
    }

    /// <summary>收起时角标停在哪儿：面里那个看不见的占位数字的中心。</summary>
    private Point HeadingFaceLevelSlotCenter() => CenterOf(HeadingFaceLevelSlot);

    /// <summary>
    /// 展开时角标停在哪儿：选中的那一格的**正中**。
    /// </summary>
    /// <remarks>
    /// 直接量那一格自己的位置——别按"帘子左缘 + 格宽 × (级别 − 0.5)"去推：格子宽窄、间距、
    /// 外边距怎么调，推算都可能差一点点（差一点点看着就是"没落正"）。
    /// 前提是量的时候帘子宽度**已经是终态**（<see cref="OpenHeadingChoices"/> 里先写宽度、
    /// <c>UpdateLayout()</c> 之后再量），否则量到的是"还挤在 0 宽里"的位置。
    /// </remarks>
    private Point HeadingChoiceCenter(int level) => CenterOf(level switch
    {
        1 => HeadingChoice1,
        2 => HeadingChoice2,
        _ => HeadingChoice3,
    });

    /// <summary>元素在角标覆盖层坐标系里的中心（覆盖层与工具栏同格，量的是元素实际排版的位置）。</summary>
    private Point CenterOf(FrameworkElement element)
    {
        Rect rect = element.TransformToVisual(HeadingBadgeLayer)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new Point(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
    }

    /// <summary>选中的那一格不画自己的数字（角标正停在它上面，两个数字会叠在一起）。</summary>
    private void SyncHeadingChoiceDigits()
    {
        int level = _toolbar.HeadingLevel;
        HeadingChoiceText1.Opacity = level == 1 ? 0 : 1;
        HeadingChoiceText2.Opacity = level == 2 ? 0 : 1;
        HeadingChoiceText3.Opacity = level == 3 ? 0 : 1;
    }

    private void ShowAllHeadingChoiceDigits()
    {
        HeadingChoiceText1.Opacity = 1;
        HeadingChoiceText2.Opacity = 1;
        HeadingChoiceText3.Opacity = 1;
    }

    /// <summary>动画收尾：先把终值写回属性，再停掉动画（否则保持值会盖住以后的直接赋值）。</summary>
    private void RunHeadingAnimation(Storyboard story, Action bake)
    {
        story.Completed += (_, _) =>
        {
            bake();
            story.Stop();
            if (ReferenceEquals(_headingStory, story))
            {
                _headingStory = null;
            }
        };

        _headingStory?.Stop(); // 放掉上一轮的保持值（上一轮已经不在跑了）
        _headingStory = story;
        story.Begin();
    }

    private static void AddHeadingAnimation(Storyboard story, DependencyObject target, string property,
        double from, double to, int milliseconds, bool isLayout, EasingMode easing = EasingMode.EaseOut)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = easing },
            EnableDependentAnimation = isLayout, // 宽度这类布局属性的动画必须显式允许
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    /// <summary>
    /// 标题控件的显性态：面里的占位、看得见的角标、选项的选中态都对齐到**常用级别**
    /// （全局设置，不是光标所在块的级别）。角标的数字在它飞行期间**不换**——落地那一下才换
    /// （见 <see cref="CollapseHeadingChoices"/> 的收尾）。
    /// </summary>
    private void UpdateHeadingVisuals()
    {
        int level = _toolbar.HeadingLevel;
        HeadingFaceLevelSlot.Text = DigitOf(level); // 看不见、但撑住 H 的位置并给角标量坐标
        if (!_headingBadgeAnimating)
        {
            UpdateHeadingBadgeText();
        }

        HeadingChoice1.IsChecked = level == 1;
        HeadingChoice2.IsChecked = level == 2;
        HeadingChoice3.IsChecked = level == 3;
    }

    private void UpdateHeadingBadgeText() => HeadingBadgeText.Text = DigitOf(_toolbar.HeadingLevel);

    private static string DigitOf(int level) => level switch
    {
        1 => "1",
        2 => "2",
        3 => "3",
        _ => string.Empty,
    };

    /// <summary>分点/勾选按钮态随光标所在块刷新（Phase 3 M2 §6.1）：光标所在块已带标记就显示为激活。</summary>
    private void OnCaretBlockChanged(object? sender, EventArgs e)
    {
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
    /// <summary>
    /// 左键：选中文字**已经是当前色** → 再点一次＝取消（按钮保留当前色，方便再刷回来）；否则刷成当前色。
    /// </summary>
    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        var color = ToColor32(_toolbar.HighlightColor);
        _editor.SetInlineBackground(_editor.SelectionHasBackground(color) ? null : color);
    }

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
        HighlightPickerLayer.Open(squareRect, _toolbar.HighlightColor);
        e.Handled = true;
    }

    /// <summary>色带松手：底色记进**全局设置**（落盘；别的便签窗口跟着换色），并刷到选中文字。</summary>
    private void ApplyHighlight(Windows.UI.Color color)
    {
        _toolbar.SetHighlightColor(color);
        _editor.SetInlineBackground(ToColor32(color));
    }

    /// <summary>底色是全局值：别的便签窗口改了，本窗口的方块跟着换色。</summary>
    private void OnToolbarHighlightColorChanged(object? sender, EventArgs e) => UpdateHighlightButtonFill();

    /// <summary>常用标题级别同上：别的便签窗口改了，本窗口的面与选项跟着换。</summary>
    private void OnToolbarHeadingLevelChanged(object? sender, EventArgs e) => UpdateHeadingVisuals();

    /// <summary>底色一律按不透明实色送进文档（RTF 颜色表没有 alpha 通道）。</summary>
    private static LumiText.Core.Documents.Color32 ToColor32(Windows.UI.Color color) =>
        new(0xFF, color.R, color.G, color.B);

    /// <summary>状态条的常规墨色（与正文副色同一档）。</summary>
    private static readonly Windows.UI.Color StatusInk = Windows.UI.Color.FromArgb(255, 0x75, 0x69, 0x7C);

    /// <summary>保存失败 / 临时提示时的警示色（与便签配色的红同一档）。</summary>
    private static readonly Windows.UI.Color StatusAlertInk = Windows.UI.Color.FromArgb(255, 0xB4, 0x2D, 0x3C);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteViewModel.StatusText))
        {
            UpdateStatusVisuals();
        }
    }

    /// <summary>
    /// 右下角保存状态（2026-10-04 改为图标）：保存中转圈、已保存对勾、失败警示；
    /// 有临时提示时改成警示 + **把提示文案露出来**（提示多半是失败信息，只藏在 Tooltip 里等于没提示）。
    /// 平时只显示图标 + 字数，完整文案（"已保存 · 42 字"）统一进 Tooltip。
    /// </summary>
    private void UpdateStatusVisuals()
    {
        bool hint = _viewModel.HasHint;
        bool saving = !hint && _viewModel.Status == SaveStatus.Saving;
        bool alert = hint || _viewModel.Status == SaveStatus.Failed;

        StatusProgress.IsActive = saving;
        StatusProgress.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = saving ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Glyph = alert ? LumiIcons.Warning : LumiIcons.Checkmark;
        StatusIcon.Foreground = new SolidColorBrush(alert ? StatusAlertInk : StatusInk);

        StatusHintText.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        StatusHintText.Text = hint ? _viewModel.StatusText : string.Empty;
        StatusCountText.Visibility = hint ? Visibility.Collapsed : Visibility.Visible;
        StatusCountText.Text = $"{_viewModel.CharacterCount} 字";

        ToolTipService.SetToolTip(StatusIcon, _viewModel.StatusText);
        ToolTipService.SetToolTip(StatusProgress, _viewModel.StatusText);
    }

    private void UpdateHighlightButtonFill() =>
        HighlightButtonFill.Background = new SolidColorBrush(_toolbar.HighlightColor);

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

    /// <summary>点亮别处＝取消取色（色带收起，不套用）；标题选项同理（收起，不改级别）。</summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (HighlightPickerLayer.IsOpen && !IsWithin(source, HighlightPickerLayer))
        {
            HighlightPickerLayer.Close(apply: false);
        }

        // 标题：点 H 本体不算"别处"（它有 Click 处理器，自己决定收起还是取消），点选项当然也不算
        if (_headingOpen && !IsWithin(source, HeadingButton) && !IsWithin(source, HeadingChoiceHost))
        {
            CollapseHeadingChoices();
        }
    }

    /// <summary>Esc 也取消（预览键隧道到根，先于编辑器的 Esc 处理）。</summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        if (HighlightPickerLayer.IsOpen)
        {
            HighlightPickerLayer.Close(apply: false);
            e.Handled = true;
        }
        else if (_headingOpen)
        {
            CollapseHeadingChoices();
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // 工具栏偏好是**单例**：不退订的话，关掉的窗口会被它的事件表一直拽着不放。
        _toolbar.HighlightColorChanged -= OnToolbarHighlightColorChanged;
        _toolbar.HeadingLevelChanged -= OnToolbarHeadingLevelChanged;

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
