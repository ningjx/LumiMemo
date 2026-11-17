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

    /// <summary>隐藏的 owner 窗口：让本窗口进 Alt+Tab 而不占任务栏（见 <see cref="HiddenOwnerWindow"/>）。</summary>
    private readonly HiddenOwnerWindow? _switcherOwner;

    private AcrylicBackdrop? _backdrop;
    private bool _isApplicationExiting;
    private bool _closeApproved;
    private bool _closeInProgress;

    /// <summary>工具栏那两个「当前值」（文字底色的颜色、常用标题级别）：全局一份、随设置落盘。</summary>
    private readonly ToolbarPreferences _toolbar;

    private const string HighlightButtonTip = "文字底色（左键点击切换，按住左键拖动取色）";

    private const string HeadingButtonTip = "字号（左键点击应用常用档，按住左键拖动选档）";

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

        // 进 Alt+Tab、不占任务栏：靠隐藏 owner 把系统那两个列表拆开（见 HiddenOwnerWindow）。
        // 排在构造函数最前头——窗口显示之后再改，任务栏要 hide→改→show 才会重新判定，
        // 而窗口是窗口管理器在构造之后才 Activate/Show 的，这里还来得及。
        _switcherOwner = HiddenOwnerWindow.AttachTo(this);

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

        // 窗口标题 = 便签标题（界面上看不见——标题栏是自绘的，但 Alt+Tab 与任务栏认它，
        // 便签一多，那里得认得出是哪一张；正文改动时跟着换，见 OnViewModelPropertyChanged）。
        Title = _viewModel.Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        ConfigureWindow();
        _backdrop = AcrylicBackdrop.Apply(this, Root);

        HighlightPickerLayer.Attach(HighlightButtonFill, HighlightLeftGroup, HighlightRightGroup);
        HighlightPickerLayer.ColorPicked += (_, color) => ApplyHighlight(color);
        HighlightPickerLayer.Opened += (_, _) => SetHighlightButtonIdle(true);
        HighlightPickerLayer.Closed += (_, _) => SetHighlightButtonIdle(false);
        HighlightButton.DragStarted += x =>
        {
            HeadingPickerLayer.Dismiss();
            HighlightPickerLayer.Open(x);
        };
        HighlightButton.DragMoved += HighlightPickerLayer.UpdateFromPointer;
        HighlightButton.DragCompleted += HighlightPickerLayer.Commit;
        HighlightButton.DragCanceled += () => HighlightPickerLayer.Close(apply: false);
        SetHighlightButtonIdle(false);

        HeadingPickerLayer.Attach(HeadingFace, HeadingLeftGroup, HeadingLeftDivider, ToolbarStatus);
        HeadingPickerLayer.LevelPicked += level =>
        {
            _toolbar.SetHeadingLevel(level);
            ApplyHeadingLevel();
        };
        HeadingPickerLayer.VisibilityChanged += SetHeadingButtonIdle;
        HeadingButton.DragStarted += x =>
        {
            HighlightPickerLayer.Dismiss();
            HeadingPickerLayer.Open(x, _toolbar.HeadingLevel);
        };
        HeadingButton.DragMoved += HeadingPickerLayer.UpdateFromPointer;
        HeadingButton.DragCompleted += HeadingPickerLayer.Commit;
        HeadingButton.DragCanceled += HeadingPickerLayer.Close;
        SetHeadingButtonIdle(false);

        _toolbar.HighlightColorChanged += OnToolbarHighlightColorChanged;
        _toolbar.HeadingLevelChanged += OnToolbarHeadingLevelChanged;
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
        // 这里**不碰** AppWindow.IsShownInSwitchers：它内部走 shell 的 ITaskbarList
        // （HrInit + AddTab/DeleteTab，见 microsoft-ui-xaml#8596），一旦设置就等于让 shell
        // 重新登记这个窗口，会把 owner 刚摘掉的任务栏按钮又装回来；explorer 不响应时它还会抛。
        // 两个列表的分家全部交给 owner 规则，见 HiddenOwnerWindow。

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

    private void OnHeadingClick(object sender, RoutedEventArgs e)
    {
        if (!HeadingButton.SuppressClick && !HeadingPickerLayer.IsOpen)
        {
            ApplyHeadingLevel();
        }
    }

    private void ApplyHeadingLevel()
    {
        if (_editor.HasSelection)
        {
            _editor.ApplyInlineHeadingSize(_toolbar.HeadingLevel);
            return;
        }

        _editor.ExecuteCommand($"h{_toolbar.HeadingLevel}");
    }

    private void UpdateHeadingVisuals() =>
        HeadingFaceLevel.Text = _toolbar.HeadingLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>分点/勾选按钮态随光标所在块刷新（Phase 3 M2 §6.1）：光标所在块已带标记就显示为激活。</summary>
    private void OnCaretBlockChanged(object? sender, EventArgs e)
    {
        BulletButton.IsChecked = _editor.CaretIsBullet;
        TodoButton.IsChecked = _editor.CaretIsTodo;
    }

    /// <summary>点击切换选中文字的当前底色。</summary>
    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        if (HighlightButton.SuppressClick || HighlightPickerLayer.IsOpen)
        {
            return;
        }

        var color = ToColor32(_toolbar.HighlightColor);
        _editor.SetInlineBackground(_editor.SelectionHasBackground(color) ? null : color);
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
        else if (e.PropertyName is nameof(NoteViewModel.Title))
        {
            // Alt+Tab 与任务栏上的那张牌子（自绘标题栏用不到它，但系统那两处认它）。
            // 便签标题是正文的投影，打字就会变——牌子跟着走，列表里才是当前的样子。
            Title = _viewModel.Title;

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

    /// <summary>展开色带期间隐藏按钮的交互外观，让按钮继续接收捕获的指针。</summary>
    private void SetHighlightButtonIdle(bool idle)
    {
        SetButtonIdle(HighlightButton, idle);
        ToolTipService.SetToolTip(HighlightButton, idle ? null : HighlightButtonTip);
    }

    private void SetHeadingButtonIdle(bool idle)
    {
        SetButtonIdle(HeadingButton, idle);
        HeadingFace.Opacity = idle ? 0 : 1;
        ToolTipService.SetToolTip(HeadingButton, idle ? null : HeadingButtonTip);
    }

    /// <summary>点亮别处＝取消取色（色带收起，不套用）；标题选项同理（收起，不改级别）。</summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (HighlightPickerLayer.IsOpen && !IsWithin(source, HighlightButton))
        {
            HighlightPickerLayer.Close(apply: false);
        }

        if (HeadingPickerLayer.IsOpen && !IsWithin(source, HeadingButton))
        {
            HeadingPickerLayer.Close();
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
        else if (HeadingPickerLayer.IsOpen)
        {
            HeadingPickerLayer.Close();
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

        // 隐藏 owner 排在 XAML 拆解**之后**：它只是把 owner 还回池子（见 HiddenOwnerWindow），
        // 但这个窗口的关闭流程里不再多插任何事，免得再和 XAML 收摊抢时序。
        try
        {
            _switcherOwner?.Dispose();
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
