using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private AcrylicBackdrop? _backdrop;
    private bool _isApplicationExiting;
    private bool _closeApproved;
    private bool _closeInProgress;

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

        _editor = new LumiEditor { HostWindow = this };
        _editor.CaretBlockChanged += OnCaretBlockChanged;
        EditorHost.Children.Add(_editor);
        _viewModel.AttachDocument(new LumiEditorDocument(_editor));
        _viewModel.TopMostChanged += OnTopMostChanged;

        Title = "LumiMemo";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        ConfigureWindow();
        _backdrop = AcrylicBackdrop.Apply(this, Root);

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

    /// <summary>标题按钮态随光标所在块刷新（Phase 3 M2 §6.1）：已在该级别显示为按下。</summary>
    private void OnCaretBlockChanged(object? sender, EventArgs e)
    {
        int level = _editor.CaretHeadingLevel;
        Heading1Button.IsChecked = level == 1;
        Heading2Button.IsChecked = level == 2;
        Heading3Button.IsChecked = level == 3;
    }

    /// <summary>块背景色的 Alpha：保住毛玻璃透出的观感约束（Phase 3 §9 R4，上限 0x40）。</summary>
    private const byte BlockBackgroundAlpha = 0x40;

    /// <summary>
    /// 段落底色色板（Phase 3 M1）：便签纸色系 × 低 Alpha 色块，作用到选区覆盖的块；
    /// 末位「无」清除。色块显示纸面色（实色），写入块的色值 = 同色 × <see cref="BlockBackgroundAlpha"/>。
    /// </summary>
    private void OnBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(10, 8, 10, 8),
        };
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        var border = new SolidColorBrush(Windows.UI.Color.FromArgb(0x50, 0x75, 0x69, 0x7C));

        foreach (NoteColor color in Enum.GetValues<NoteColor>())
        {
            var paper = NoteColorPalette.Paper(color);
            var tint = new LumiText.Core.Documents.Color32(BlockBackgroundAlpha, paper.R, paper.G, paper.B);
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(paper),
                BorderThickness = new Thickness(1),
                BorderBrush = border,
            };
            ToolTipService.SetToolTip(swatch, NoteColorPalette.DisplayName(color));
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                _editor.SetBlockBackground(tint);
            };
            panel.Children.Add(swatch);
        }

        var clear = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = border,
            Content = new TextBlock { Text = "无", FontSize = 10 },
        };
        ToolTipService.SetToolTip(clear, "清除底色");
        clear.Click += (_, _) =>
        {
            flyout.Hide();
            _editor.SetBlockBackground(null);
        };
        panel.Children.Add(clear);

        flyout.ShowAt(button);
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
