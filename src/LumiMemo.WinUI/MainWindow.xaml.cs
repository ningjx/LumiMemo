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

    /// <summary>工具栏「常用标题」按钮的悬停提示（与 MainWindow.xaml 里那串字保持一致）：
    /// 展开期间要摘掉——那时它已经不是"一个按钮"了，提示会浮在刚露出来的 1/2/3 上面。</summary>
    private const string HeadingButtonTip = "常用标题（左键＝选中文字则放大成该级字号／否则整段设为该级标题；右键＝改常用级别）";

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
        _headingBadgeRestingScale = HeadingFaceLevelSlot.FontSize / HeadingBadgeText.FontSize;

        // 角标是浮层上的元素，位置得自己算：每次布局完校一次——它要跟着 H 一起被推走
        // （底色色带展开时整组会右移），展开期间还要停在选中的那个格子上。
        // 顺序有讲究：先把展开的那一排对齐到"下角标"的高度，再校角标位置（它量的是那一排）。
        HeadingBadgeLayer.LayoutUpdated += (_, _) =>
        {
            AlignHeadingChoicesWithSubscript();
            SyncHeadingBadge();
        };
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
    /// 标题区布局：文字按自然宽铺，遮罩（进度圈/刷新按钮）的位置 = min(文字自然宽, 限位)——
    /// 随窗口宽度连续变化，拖动时不按字符跳变。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 装不下时遮罩整块顶到限位（刷新按钮右缘与图标组同距 3px），文字在它左边按<strong>字符</strong>截断，
    /// 省略号由 <see cref="TextBlock.TextTrimming"/> 画在限位之内；装得下时遮罩紧贴文字右缘。
    /// </para>
    /// <para>
    /// <strong>曾经这里用的是「文字渐隐」</strong>（给 <c>Foreground</c> 铺一段相对坐标的渐变刷），
    /// 但那个做法在 WinUI 里不可靠：标题一旦中英数混排，字体回退会把文字拆成多个 glyph run，
    /// 而渐变刷的相对坐标是<strong>按每个 run 的边界</strong>铺的——几段文字就有几道渐隐
    /// （2026-10-05 实测：数字混排、字母混排都能触发，截图里标题被切成一段亮一段灭）。
    /// 换 <see cref="TextBlock.TextTrimming"/> 之后不依赖 run 怎么切，也不会切出半个字。
    /// </para>
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
        // 量之前必须先撤掉上限——否则量到的是被上一轮 maxWidth 裁过之后的宽。
        TitleText.MaxWidth = double.PositiveInfinity;
        TitleText.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        double natural = TitleText.DesiredSize.Width;

        bool truncated = natural + overlayWidth > column;
        double overlayX;

        if (truncated)
        {
            overlayX = Math.Max(0, column - overlayWidth);

            // 上限设在遮罩左缘：文字因此永远不会钻到刷新按钮底下。
            TitleText.MaxWidth = overlayX;
            TitleText.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        else
        {
            overlayX = natural;
            TitleText.TextTrimming = TextTrimming.None;
        }

        TitleOverlay.Margin = new Thickness(overlayX, 0, 0, 0);
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

    /// <summary>角标停在收起位（H 的下角标）时的缩放：与面里那个占位数字的字号对齐（两个字号都来自 XAML）。</summary>
    private readonly double _headingBadgeRestingScale;

    /// <summary>角标正被动画驱动：既别让 <see cref="SyncHeadingBadge"/> 改写它的位置，也别换它的数字。</summary>
    private bool _headingBadgeAnimating;

    /// <summary>角标已"入库"（展开落定后把数字交还给了格子）。这时它是藏着的、位置无所谓，收起时会重新摆。</summary>
    private bool _headingBadgeParked;

    /// <summary>上次把角标摆到哪儿了（没变就不写变换，免得每次布局都白重画一遍）。</summary>
    private (double X, double Y, double Scale) _headingBadgePlaced = (double.NaN, double.NaN, double.NaN);

    /// <summary>右键点 H：1/2/3 在它右边向右展开（H 自己不动）。</summary>
    private void OnHeadingRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        OpenHeadingChoices();
    }

    /// <summary>
    /// 左键点 H 本体：把**常用级别**应用一次（选了字＝放大选中的文字；没选＝整段设为该级标题）。
    /// 展开期间点它＝收起。
    /// </summary>
    private void OnHeadingClick(object sender, RoutedEventArgs e)
    {
        if (_headingOpen)
        {
            CollapseHeadingChoices();
            return;
        }

        ApplyHeadingLevel();
    }

    /// <summary>
    /// 把常用级别应用一次：**选了字**就把选中的文字放大成这一档标题的字号（行内字号比，段落类型不变）；
    /// **没选字**就把整段设成这一级标题。两条路都跟"点一下 H"完全一样——右键菜单里选完一档
    /// 也走这里（与底色按钮"选完就刷上去"一致）。两条命令自带 toggle 语义：已经在这一档就回退。
    /// </summary>
    private void ApplyHeadingLevel()
    {
        if (_editor.HasSelection)
        {
            _editor.ApplyInlineHeadingSize(_toolbar.HeadingLevel);
            return;
        }

        _editor.ExecuteCommand($"h{_toolbar.HeadingLevel}");
    }

    /// <summary>
    /// 左键点选项：把它设为**常用级别**（全局保存，下次打开/新建便签还是它），
    /// 然后**顺手应用一次**——跟底色按钮"选完就刷上去"一致，不用再点一下 H。
    /// </summary>
    private void OnHeadingChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string level }
            && int.TryParse(level, out int parsed))
        {
            _toolbar.SetHeadingLevel(parsed);
            ApplyHeadingLevel();
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
        _headingBadgeParked = false; // 上一次展开落定时它入过库；收起那一趟已经把它摆回家并露出来了

        // 展开期间 H **不画自己的背景**（悬停/按下的底色、边框、焦点框全熄掉）——跟左边那个
        // 底色按钮一个思路：这一排露出来的是"H + 1/2/3"一整套，H 上再挂一圈悬停框就不成一体了。
        // 但它**仍然可点**（点一下＝收起），所以不能像底色按钮那样把命中测试整个关掉。
        SetHeadingButtonActive(true);

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

            // 落地：把数字**交还给格子自己**（角标退场）。角标与格子里的数字是两条渲染路径，
            // 差不到 1dip 的那点永远消不掉；交了之后"选中的数字"和旁边两个就是同一种元素，差多少都看不出来。
            // 同一帧里"亮回数字 + 藏起角标"，看不出交接。
            ShowAllHeadingChoiceDigits();
            ParkHeadingBadge();
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

        // 起飞前把它**瞬移**到要离开的那一格上：落地后它一直"入库"藏着、位置停在家里，
        // 而格子自己的数字正显示着——同帧里"藏数字 + 摆角标 + 露角标"，看见的就是"它本来就在那儿"。
        _headingBadgeParked = false;
        SyncHeadingChoiceDigits();
        PlaceHeadingBadge(HeadingChoiceCenter(_toolbar.HeadingLevel), 1d);

        Point badgeTarget = HeadingFaceLevelSlotCenter();

        var story = new Storyboard();
        // From 取"当前值"：半途打断也能从眼下这一帧接着收
        AddHeadingAnimation(story, HeadingChoiceHost, "Width", HeadingChoiceHost.Width, 0d, HeadingCollapseMs, isLayout: true);
        AddHeadingBadgeAnimations(story, badgeTarget, _headingBadgeRestingScale, HeadingCollapseMs);
        RunHeadingAnimation(story, () =>
        {
            HeadingChoiceHost.Width = 0d;
            SetHeadingButtonActive(false); // 选项没了，H 恢复成一个普通按钮（悬停底色/提示都回来）
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

        // 量到位置之后才露出来（否则头一帧它会闪在工具栏左上角）；"入库"时就该是藏着的
        HeadingBadge.Opacity = _headingBadgeParked ? 0 : 1;
    }

    /// <summary>
    /// 角标"入库"：展开落定后把数字**交还给格子自己的 TextBlock**，角标藏起来。
    /// </summary>
    /// <remarks>
    /// 角标和格子里的数字是**两个不同元素、两条渲染路径**——哪怕两边都居中、哪怕量的是数字自己，
    /// 也总会差不到 1dip（实机反复出现，而且随字号/框大小变）。落定后让格子自己画，
    /// 就等于"和旁边两个一模一样"，差多少都为零，**以后怎么调都不跑偏**。
    /// 收起时再把它瞬移回目标格上起飞（见 <see cref="CollapseHeadingChoices"/>）。
    /// </remarks>
    private void ParkHeadingBadge()
    {
        _headingBadgeParked = true;
        HeadingBadge.Opacity = 0;
    }

    /// <summary>每次布局完校一次角标与选项数字——位置/选中项变了就自己跟上。</summary>
    private void SyncHeadingBadge()
    {
        // 入库期间不用管它（藏着的，位置无所谓）；收起时会重新摆
        if (_headingBadgeAnimating || _headingBadgeParked)
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

    /// <summary>
    /// 把展开的 1/2/3 整排**对到 H 那个下角标的高度**上。
    /// </summary>
    /// <remarks>
    /// 为什么要对：角标从"下角标"飞到"选项格子里"，两端高度一致时它就是**纯横向平移**
    /// （高度全程不变），看着才像"那个数字挪了个位置"，而不是边飞边长边上下跳。
    /// 用 <see cref="HeadingChoiceHostShift"/> 那个 `TranslateTransform` 调、不动外框的 `Margin`：
    /// 位移**精确**（居中排布下 Margin 会被打对折），而且每次布局按实测重算——
    /// 字号、行高、下角标位置、格子大小怎么改都不会跑偏。
    /// </remarks>
    private void AlignHeadingChoicesWithSubscript()
    {
        double current = HeadingChoiceHostShift.Y;

        // 量到的位置里已经含了当前位移，先减掉它，得到"位移为 0 时"的位置，再定目标
        double digitY = CenterOf(HeadingChoiceText1).Y - current;
        double target = HeadingFaceLevelSlotCenter().Y - digitY;

        if (Math.Abs(current - target) < 0.01)
        {
            return;
        }

        HeadingChoiceHostShift.Y = target;
    }

    /// <summary>收起时角标停在哪儿：面里那个看不见的占位数字的中心。</summary>
    private Point HeadingFaceLevelSlotCenter() => CenterOf(HeadingFaceLevelSlot);

    /// <summary>
    /// 落点再往下压一点点（dip）。**现在是 0**——两边的渲染已经对齐了，不需要补。
    /// </summary>
    /// <remarks>
    /// 早先这里要补 0.42（两点标定：0 偏高、0.85 偏低），根因是**两边走了两条渲染路径**：
    /// 格子里的数字是按钮 `ContentPresenter` 的直接内容，`LineHeight` 被它顶掉（数字在框里偏低 0.85dip），
    /// 而角标是浮层 `Border` 的直接子元素、样式正常生效（居中）。
    /// 现在两边结构一样（数字都套了一层容器 + `VerticalAlignment=Center`），落点自然对得上。
    /// **真要再微调就动这个数**：正数往下、负数往上；只作用于"展开时飞过去停在哪儿"，
    /// 收起时那个下角标走另一条路（量占位），不受影响。
    /// </remarks>
    private const double HeadingChoiceLandingDrop = 0;

    /// <summary>
    /// 展开时角标停在哪儿：**选中那一格里的那个数字**的中心，再按
    /// <see cref="HeadingChoiceLandingDrop"/> 压一点。
    /// </summary>
    /// <remarks>
    /// 量的是数字、不是格子：格子是 16×16，而 13 号数字的行框比它高——两者"居中"的位置差着半格。
    /// 量数字就与"那一格自己怎么摆这个数字"无关了。
    /// 前提：量之前帘子宽度必须已是终态（<see cref="OpenHeadingChoices"/> 里先写宽度、
    /// <c>UpdateLayout()</c> 之后再量），否则量到的是"还挤在 0 宽里"的位置。
    /// </remarks>
    private Point HeadingChoiceCenter(int level)
    {
        Point center = CenterOf(level switch
        {
            1 => HeadingChoiceText1,
            2 => HeadingChoiceText2,
            _ => HeadingChoiceText3,
        });

        return new Point(center.X, center.Y + HeadingChoiceLandingDrop);
    }

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

    /// <summary>右键：色带在按钮栏里向右展开（右侧按钮被推开），方块滑到当前色的位置上当滑块。</summary>
    private void OnHighlightRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (HighlightPickerLayer.IsOpen)
        {
            return;
        }

        // 量三样东西（都在**隐藏方块之前**量）：方块的矩形、**收起时按钮右缘到右分割线的净空**、
        // 右分割线现在的位置。Collapsed 的元素量不到有效坐标（这是上一版动画失效的根因）。
        Rect squareRect = RectInRoot(HighlightButtonFill);
        double gap = RectInRoot(HighlightRightDivider).X - RectInRoot(HighlightButton).Right;
        double stripRight = squareRect.X + HighlightPicker.StripWidthDips; // 色带左缘对齐方块左缘

        // 让位宽：**先按算式给初值，再强制排一次版、量真实结果修正一次**。
        // 为什么不直接信算式：占位块变宽 1dip，右边到底是不是正好右移 1dip，取决于
        // StackPanel 的 Spacing 与那对负外边距怎么组合——这一处推错过两回，所以改成"量"。
        // 修正之后：色带右端到右分割线的距离，**正好等于**左侧那个净空（gap）。
        double pushWidth = Math.Max(0, stripRight + gap- RectInRoot(HighlightRightDivider).X);

        HighlightStripSpacer.Width = pushWidth;
        Root.UpdateLayout(); // 强制排一次：下面量到的就是"展开后"的真实位置
        pushWidth = Math.Max(0,
            pushWidth + gap + 8 - (RectInRoot(HighlightRightDivider).X - stripRight));
        HighlightStripSpacer.Width = 0; // 还原成起点：展开动画从 0 开始长

        HighlightButtonFill.Visibility = Visibility.Collapsed; // 方块交给滑块接管：视觉上就是它滑过去了
        SetHighlightButtonIdle(true);
        HighlightPickerLayer.Open(squareRect, _toolbar.HighlightColor, pushWidth);
        e.Handled = true;
    }

    /// <summary>元素在窗口根坐标系里的矩形。量坐标一律走它（别拿 Canvas.Left/Top 那套去推）。</summary>
    private Rect RectInRoot(FrameworkElement element) =>
        element.TransformToVisual(Root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

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
        else if (e.PropertyName is nameof(NoteViewModel.Title))
        {
            // 标题换了，标题区就得重算——遮罩位置与渐隐都是按文字的自然宽算出来的，
            // 而「自然宽变了」不在任何 SizeChanged 的触发源里（窗口尺寸没动），
            // 于是整块布局会一直停在旧标题上。
            //
            // 症状：自动生成的标题只露出前一两个字，手动拖一下窗口又好了。
            // 来路是标题区的计算依赖文字自然宽：生成完成时先收进度圈（触发一次重算，
            // 可那时量到的还是旧的长标题，按它算出的渐隐是「到 15% 全透明」），
            // 随后文字才换成新的短标题，那段按比例铺开的渐变刷就把新标题几乎全吃掉了；
            // 遮罩也停在旧位置，于是标题与刷新按钮之间空出一大截。
            //
            // 推迟一轮再算：x:Bind 与本处理器的先后没有保证（本订阅在构造函数里、
            // 早于绑定的接入），此刻 TextBlock 里可能还是旧文字，量出来的自然宽就是错的。
            DispatcherQueue.TryEnqueue(UpdateTitleLayout);
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
    /// 把按钮"熄灭"：悬停/按下的底色与边框、悬停时的前景、焦点框全在**元素级**覆盖成常量，
    /// 一直保持到 <paramref name="idle"/>=false 收回来。
    /// </summary>
    /// <remarks>
    /// 为什么非要元素级覆盖：光设 <c>IsHitTestVisible = false</c> 不够——指针**已经**在按钮上，
    /// PointerOver 是**粘住的**（元素不再收输入，也就等不到 PtrExited 来复位），那圈边框会一直
    /// 画在刚展开的东西上面（截图里那个圆角空框）。焦点视觉同理：WinUI 的 FocusVisual 也是
    /// "外扩一圈圆角框"，Tab 过来就会露出来。
    /// </remarks>
    private static void SetButtonIdle(ButtonBase button, bool idle)
    {
        if (idle)
        {
            var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));

            foreach (string key in IdleOverriddenBrushKeys)
            {
                button.Resources[key] = transparent;
            }

            // 悬停/按下时模板还会把前景换一档（图标跟着变色）——一并按住，否则熄灭的按钮会"变浅"
            button.Resources["ButtonForegroundPointerOver"] = ink;
            button.Resources["ButtonForegroundPressed"] = ink;

            button.UseSystemFocusVisuals = false;
            VisualStateManager.GoToState(button, "Normal", false);
        }
        else
        {
            foreach (string key in IdleOverriddenBrushKeys)
            {
                button.Resources.Remove(key);
            }

            button.Resources.Remove("ButtonForegroundPointerOver");
            button.Resources.Remove("ButtonForegroundPressed");
            button.UseSystemFocusVisuals = true;
        }
    }

    /// <summary><see cref="SetButtonIdle"/> 覆盖成透明的那些模板画刷键。</summary>
    private static readonly string[] IdleOverriddenBrushKeys =
    [
        "ButtonBackgroundPointerOver",
        "ButtonBackgroundPressed",
        "ButtonBorderBrushPointerOver",
        "ButtonBorderBrushPressed",
        "FocusVisualPrimaryBrush",
        "FocusVisualSecondaryBrush",
    ];

    /// <summary>展开色带期间把底色按钮整个让位（连命中测试一起关掉：那时候点它就是"点别处"）。</summary>
    private void SetHighlightButtonIdle(bool idle)
    {
        SetButtonIdle(HighlightButton, idle);
        HighlightButton.IsHitTestVisible = !idle;
        ToolTipService.SetToolTip(HighlightButton, idle ? null : HighlightButtonTip);
    }

    /// <summary>
    /// 展开 1/2/3 期间把 H "熄灭"（背景/边框/焦点框都不画）。
    /// 与底色按钮的区别：**它仍然可点**——点一下＝收起，所以命中测试照旧开着。
    /// </summary>
    private void SetHeadingButtonActive(bool active)
    {
        SetButtonIdle(HeadingButton, active);
        ToolTipService.SetToolTip(HeadingButton, active ? null : HeadingButtonTip);
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
