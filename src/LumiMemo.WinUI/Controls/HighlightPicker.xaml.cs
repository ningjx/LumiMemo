using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.Foundation;
using Windows.UI;

namespace LumiMemo.WinUI.Controls;

/// <summary>
/// 文字底色取色器（展开式色带，2026-10-04 设计）：工具栏上那个**圆角方块**就是按钮本体；
/// 右键在按钮栏里展开一条**细色相带**（按钮底下那层，占位块把右侧按钮推开），
/// 方块**滑动到当前色的位置上当滑块**（滑块＝当前色实时显示）；拖动无级取色，
/// 松手后色带缩回、方块回位，并把新颜色交回宿主（<see cref="ColorPicked"/>）。
/// </summary>
/// <remarks>
/// <para><b>色带本体归宿主</b>：它插在工具栏里（<see cref="Open"/> 前由 <see cref="AttachStrip"/> 接上），
/// 这样"展开"就是一次普通布局变化——右侧按钮由 StackPanel 连同占位块一起推开，不用手写位移动画。
/// 本控件只管滑块（跟着色相位置走的那块）。</para>
/// <para><b>坐标</b>：滑块是覆盖层里的元素，位置用 TranslateTransform（Canvas.Left/Top 不能动画）。
/// 色带的窗口坐标在展开那一刻量一次——展开期间它的左缘不动。</para>
/// <para><b>变形的先后</b>：滑块的 TransformGroup 必须"先缩放、后平移"，且原点在中心——
/// XAML 按顺序左乘，反过来写平移量会被缩放一起放大。</para>
/// <para><b>取色口径</b>：色带就是按 <see cref="HueSaturation"/>/<see cref="HueValue"/> 画出来的，
/// 所以"带上的颜色"和"选到的颜色"完全一致（所见即所得）。</para>
/// <para><b>三条踩过的坑</b>（改这里之前先读）：
/// ① 自绘的可点元素**必须给 Background**——没有画刷的 Border 既不显色也不参与命中测试；
/// ② 量元素坐标要在它**可见**时做（Collapsed 的元素量不到有效矩形），所以方块的矩形由宿主在
/// 隐藏它之前量好传进来；
/// ③ Storyboard 播完会**保持**动画值、盖住之后的直接赋值（滑块就拖不动了）——换场前先停、
/// 播完先把终值写回属性再停（见 <see cref="_running"/>）。</para>
/// </remarks>
internal sealed partial class HighlightPicker : UserControl
{
    // ★ 色带与取色参数（随手感调）
    /// <summary>展开后的色带长度（dip）：从方块中心起向右变长。</summary>
    public const double StripWidthDips = 76;

    /// <summary>按钮栏里那个占位块要变宽多少：色带长度 − 25。
    /// 那个 25 = "按钮格 + Spacing"的固定量 33 − 留给后面分隔线的净空 8。</summary>
    private const double PushWidthDips = StripWidthDips - 25;

    /// <summary>色相带右端：红 0° → 紫 300°。</summary>
    private const float HueEnd = 300f;

    /// <summary>取色的饱和度/明度：定成"当文字底色不刺眼"的一档。</summary>
    private const double HueSaturation = 0.55;
    private const double HueValue = 0.99;

    /// <summary>渐变色标的段数（0..300° 每 20° 一段）。</summary>
    private const int HueSteps = 15;

    // ★ 几何与动画（随手感调）
    /// <summary>
    /// 滑块元素边长＝收起时的尺寸，**必须与工具栏上那个方块（`HighlightButtonFill`）一致**：
    /// 展开时是滑块从方块手里接管，尺寸对不上就会在那一瞬间跳一下。
    /// </summary>
    private const double CollapsedKnobSize = 16;

    /// <summary>
    /// 展开后的视觉边长：与收起时相同，也就是"工具栏那个方块"和"待办勾选框的墨迹"共用的那一档（16）。
    /// （早先收起 18 / 展开 16 是为了"滑过去的时候缩一点"；方块本身收到 16 之后就不缩了。）
    /// 注意别拿 FontIcon 的 FontSize 当尺寸——Fluent 图标在 20 的字号里只画 ~16dip，四周是留白。
    /// </summary>
    private const double ExpandedKnobSize = 16;

    private const int ExpandMs = 250;   // 进场（≈ ControlNormalAnimationDuration）
    private const int CollapseMs = 167; // 退场（≈ ControlFastAnimationDuration）
    private const int GlideMs = 120;    // 点色带取色后，滑块滑到落点的时长（让人看清选了哪个色）

    private FrameworkElement? _strip;  // 工具栏里的色带（AttachStrip 给，Open 时读它的位置与宽高）
    private Border? _stripHost;
    private FrameworkElement? _spacer; // 按钮栏里的占位块（与色带同步变宽，推开右侧按钮）
    private double _stripLeft;         // 色带左缘在覆盖层坐标里的位置（展开期间不变）
    private double _stripTop;
    private double _stripHeight;

    /// <summary>方块在窗口里的矩形（宿主在隐藏它之前量好；收起时滑块要回到这里）。</summary>
    private Rect _squareRect;

    private Color _pending;            // 正在拖的颜色（松手才交回宿主）
    private bool _open;
    private bool _dragging;
    private bool _gradientBuilt;

    /// <summary>正在跑的动画：Storyboard 播完会保持动画值、盖住之后的直接赋值，所以要记账（见类注释）。</summary>
    private Storyboard? _running;

    public HighlightPicker()
    {
        InitializeComponent();

        // 按下：滑块会在 Layer 上冒泡；色带（在工具栏里）由 AttachStrip 单独接
        Layer.PointerPressed += OnAnyPointerPressed;
        Layer.PointerMoved += OnAnyPointerMoved;
        Layer.PointerReleased += OnAnyPointerReleased;
        Layer.PointerCaptureLost += OnAnyPointerCaptureLost;
    }

    /// <summary>松手选定了新颜色（在收起动画之前抛出，好让宿主的方块同步换色）。</summary>
    public event EventHandler<Color>? ColorPicked;

    /// <summary>收起动画播完（宿主据此把工具栏那个方块放回来）。</summary>
    public event EventHandler? Closed;

    /// <summary>是否已展开。</summary>
    public bool IsOpen => _open;

    /// <summary>
    /// 接上宿主按钮栏里的两件东西：色带（<paramref name="strip"/>，按钮**底下**那层，负责画）
    /// 与占位块（<paramref name="spacer"/>，按钮栏里的普通成员，负责把右侧按钮推开）。
    /// 两者同步变宽，视觉上就是"色带从方块处向右长出来、右侧按钮让位"。
    /// </summary>
    public void AttachStrip(Border strip, FrameworkElement spacer)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(spacer);
        _stripHost = strip;
        _spacer = spacer;
        strip.PointerPressed += OnAnyPointerPressed;
    }

    /// <summary>
    /// 展开：色带从 <paramref name="squareRect"/>（宿主在**隐藏方块之前**量好的窗口矩形）
    /// 处向右长到 <see cref="StripWidthDips"/>；滑块从方块的大小与位置滑到
    /// <paramref name="current"/> 在带上的位置，并放大到展开尺寸。
    /// </summary>
    public void Open(Rect squareRect, Color current)
    {
        _running?.Stop(); // 放掉上一轮的保持值
        _running = null;

        if (_stripHost is not { } strip)
        {
            return; // 没接色带：什么都不做（宿主接线漏了也不会崩）
        }
        _strip = strip;
        _squareRect = squareRect;
        _pending = current;
        _open = true;
        _dragging = false;

        BuildHueGradient(strip);

        // 色带左缘对齐**方块左缘**：两边都在窗口坐标系里量（平移量的差值与坐标系无关），再向右长
        if (strip.RenderTransform is TranslateTransform shift)
        {
            shift.X = 0; // 量之前归零：TransformToVisual 会把变换算进去
            double stripLeft = strip.TransformToVisual(Layer)
                .TransformBounds(new Rect(0, 0, strip.ActualWidth, strip.ActualHeight)).X;
            shift.X = squareRect.X - stripLeft;
        }

        // 色带的窗口坐标在展开那一刻量一次——展开期间它的左缘不动。
        var stripOrigin = strip.TransformToVisual(Layer).TransformPoint(new Point(0, 0));
        _stripLeft = stripOrigin.X;
        _stripTop = stripOrigin.Y;
        _stripHeight = strip.ActualHeight;

        // 命中带：带子上下各留 14——细带只有 6dip 高，按视觉位置点很容易点空，
        // 点空会走"点别处＝取消"那条路（颜色没改就收起）。左右**不外扩**：右边紧邻分隔线/H1，
        // 外扩会盖住它们的边缘。滑块在它上面，事件都走同一套处理。
        const double hitPadY = 14d;
        HitZone.Width = StripWidthDips;
        HitZone.Height = _stripHeight + (hitPadY * 2d);
        HitZoneShift.X = _stripLeft;
        HitZoneShift.Y = _stripTop - hitPadY;
        HitZone.Visibility = Visibility.Visible;

        // 基准值先设成**终态**：动画只负责过程，某条没跑成也不会留下坏状态。
        // （同一帧里 Begin() 就挂上了动画，所以不会闪终态——首帧读到的仍是 From。）
        strip.Width = StripWidthDips;
        strip.Opacity = 1;
        if (_spacer is { } pushFinal)
        {
            pushFinal.Width = PushWidthDips;
        }
        SetKnobVisual(KnobCenterXFor(current));
        Knob.Opacity = 1;
        Knob.Background = new SolidColorBrush(current); // 滑块＝当前色（没有画刷的 Border 不参与命中测试）
        Layer.Visibility = Visibility.Visible;

        var story = new Storyboard();
        Add(story, strip, "Width", 0d, StripWidthDips, ExpandMs);
        Add(story, strip, "Opacity", 0d, 1d, ExpandMs);
        if (_spacer is { } push)
        {
            Add(story, push, "Width", 0d, PushWidthDips, ExpandMs); // 与色带同步：右侧按钮让位
        }
        Add(story, Knob, "Opacity", 0d, 1d, ExpandMs);
        Add(story, KnobShift, "X", CollapsedKnobLeft, KnobCenterXFor(current) - (CollapsedKnobSize / 2), ExpandMs);
        Add(story, KnobShift, "Y", CollapsedKnobTop, KnobTop, ExpandMs);
        Add(story, KnobScale, "ScaleX", 1d, ExpandedKnobScale, ExpandMs);
        Add(story, KnobScale, "ScaleY", 1d, ExpandedKnobScale, ExpandMs);
        story.Completed += (_, _) => Finish(story, BakeExpanded);
        _running = story;
        story.Begin();
    }

    /// <summary>收起（无论有没有选到色）。<paramref name="apply"/> 为真时先抛出 <see cref="ColorPicked"/>。</summary>
    public void Close(bool apply)
    {
        if (!_open || _strip is not { } strip)
        {
            return;
        }
        _open = false;
        _dragging = false;

        if (apply)
        {
            ColorPicked?.Invoke(this, _pending);
        }

        var story = new Storyboard();
        // From 取"当前值"：半途打断也能从眼下这一帧接着收（读属性拿到的是动画中的实际值）
        Add(story, strip, "Width", strip.Width, 0d, CollapseMs);
        Add(story, strip, "Opacity", strip.Opacity, 0d, CollapseMs);
        if (_spacer is { } push)
        {
            Add(story, push, "Width", push.Width, 0d, CollapseMs);
        }
        Add(story, Knob, "Opacity", Knob.Opacity, 0d, CollapseMs);
        Add(story, KnobShift, "X", KnobShift.X, CollapsedKnobLeft, CollapseMs);
        Add(story, KnobShift, "Y", KnobShift.Y, CollapsedKnobTop, CollapseMs);
        Add(story, KnobScale, "ScaleX", KnobScale.ScaleX, 1d, CollapseMs);
        Add(story, KnobScale, "ScaleY", KnobScale.ScaleY, 1d, CollapseMs);
        story.Completed += (_, _) =>
        {
            Finish(story, BakeCollapsed);
            Closed?.Invoke(this, EventArgs.Empty);
        };

        _running?.Stop();
        _running = story;
        story.Begin();
    }

    /// <summary>动画收尾：先把终值写回属性，再停掉动画（否则保持值会盖住以后的直接赋值）。</summary>
    private void Finish(Storyboard story, Action bake)
    {
        bake();
        story.Stop();
        if (ReferenceEquals(_running, story))
        {
            _running = null;
        }
    }

    /// <summary>
    /// 松手：滑块还没到落点就先短促地滑过去（≤ <see cref="GlideMs"/>，让人看清选了哪个色），
    /// 到了再收起；拖拽期间滑块本来就跟着指针，直接收。
    /// </summary>
    private void GlideThenClose()
    {
        if (_strip is null)
        {
            return;
        }

        double target = KnobCenterXFor(_pending) - (CollapsedKnobSize / 2);
        if (Math.Abs(KnobShift.X - target) < 0.5)
        {
            Close(apply: true);
            return;
        }

        var story = new Storyboard();
        Add(story, KnobShift, "X", KnobShift.X, target, GlideMs);
        story.Completed += (_, _) =>
        {
            Finish(story, () => SetKnobVisual(KnobCenterXFor(_pending)));
            Close(apply: true);
        };
        _running?.Stop();
        _running = story;
        story.Begin();
    }

    private void BakeExpanded()
    {
        if (_strip is { } strip)
        {
            strip.Width = StripWidthDips;
            strip.Opacity = 1;
        }
        if (_spacer is { } push)
        {
            push.Width = PushWidthDips;
        }
        SetKnobVisual(KnobCenterXFor(_pending));
        Knob.Opacity = 1;
    }

    private void BakeCollapsed()
    {
        if (_strip is { } strip)
        {
            strip.Width = 0;
            strip.Opacity = 0;
        }
        if (_spacer is { } push)
        {
            push.Width = 0;
        }
        KnobShift.X = CollapsedKnobLeft;
        KnobShift.Y = CollapsedKnobTop;
        KnobScale.ScaleX = 1d; // 收起＝原尺寸（16，与工具栏那个方块一致）
        KnobScale.ScaleY = 1d;
        Knob.Opacity = 0;
        HitZone.Visibility = Visibility.Collapsed;
        Layer.Visibility = Visibility.Collapsed;
    }

    // ---- 拖拽取色 ----

    /// <summary>按下：滑块（Layer 的子元素）与色带（工具栏里那位）共用同一个入口。</summary>
    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_open || !e.GetCurrentPoint(Layer).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        _dragging = true;
        Layer.CapturePointer(e.Pointer); // 捕获在覆盖层上：后续移动都回到这里，色带那边不用管
        UpdateFromPointer(e);
    }

    private void OnAnyPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        e.Handled = true;
        UpdateFromPointer(e);
    }

    private void OnAnyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        e.Handled = true;
        Layer.ReleasePointerCapture(e.Pointer);
        GlideThenClose(); // 松手：滑块先滑到落点（让人看清选了哪个色），到了再收起
    }

    private void OnAnyPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Close(apply: true);
        }
    }

    /// <summary>按指针位置更新滑块（指针到色带左缘的距离 → 色相；滑块填充实时跟着走）。</summary>
    private void UpdateFromPointer(PointerRoutedEventArgs e)
    {
        double x = e.GetCurrentPoint(Layer).Position.X;
        double travel = Math.Max(1d, StripWidthDips - ExpandedKnobSize);
        double t = Math.Clamp((x - _stripLeft - (ExpandedKnobSize / 2)) / travel, 0d, 1d);
        _pending = FromHue((float)(t * HueEnd));

        SetKnobVisual(KnobCenterXFor(_pending));
        Knob.Background = new SolidColorBrush(_pending);
    }

    // ---- 几何小工具 ----

    /// <summary>展开后滑块的顶边：色带很细（6dip），滑块上下都探出去——就是滑块的样式。</summary>
    private double KnobTop => _stripTop + ((_stripHeight - CollapsedKnobSize) / 2);

    /// <summary>展开时的缩放：现在与收起同尺寸，所以是 1（不再缩放；常量改回一大一小就自动生效）。</summary>
    private double ExpandedKnobScale => ExpandedKnobSize / CollapsedKnobSize;

    private double CollapsedKnobLeft => _squareRect.X + (_squareRect.Width / 2) - (CollapsedKnobSize / 2);

    private double CollapsedKnobTop => _squareRect.Y + (_squareRect.Height / 2) - (CollapsedKnobSize / 2);

    /// <summary>某个颜色在色带上的滑块中心 X（按色相定位；两端留半个滑块）。</summary>
    private double KnobCenterXFor(Color color)
    {
        float hue = HueOf(color);
        double t = Math.Clamp(hue / HueEnd, 0f, 1f);
        return _stripLeft + (ExpandedKnobSize / 2) + (t * (StripWidthDips - ExpandedKnobSize));
    }

    private void SetKnobVisual(double centerX)
    {
        KnobShift.X = centerX - (CollapsedKnobSize / 2);
        KnobShift.Y = KnobTop;
        KnobScale.ScaleX = ExpandedKnobScale;
        KnobScale.ScaleY = ExpandedKnobScale;
    }

    /// <summary>色带的色标只铺一次（宿主每次展开都给同一条）。</summary>
    private void BuildHueGradient(Border strip)
    {
        if (_gradientBuilt || strip.Background is not LinearGradientBrush brush)
        {
            return;
        }
        for (int i = 0; i <= HueSteps; i++)
        {
            double offset = (double)i / HueSteps;
            brush.GradientStops.Add(new GradientStop
            {
                Offset = offset,
                Color = FromHue((float)(offset * HueEnd)),
            });
        }
        _gradientBuilt = true;
    }

    // ---- 色相 ↔ RGB（S/V 固定，见类注释）----

    private static Color FromHue(float hue)
    {
        double h = ((hue % 360f) + 360f) % 360f;
        double c = HueValue * HueSaturation;
        double x = c * (1 - Math.Abs(((h / 60d) % 2d) - 1));
        double m = HueValue - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static float HueOf(Color color)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double span = max - min;
        if (span <= 0.0001)
        {
            return 0f; // 灰：当作红端
        }
        double hue = max == r ? 60 * (((g - b) / span) % 6)
            : max == g ? 60 * (((b - r) / span) + 2)
            : 60 * (((r - g) / span) + 4);
        return (float)(((hue % 360d) + 360d) % 360d);
    }

    // ---- 动画小工具（与编辑器里那套写法保持一致）----

    private static void Add(Storyboard story, DependencyObject target, string property,
        double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // 宽度这类布局属性的动画必须显式允许（同 ScrollBarReveal 的宽度动画）
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }
}
