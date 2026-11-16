using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LumiMemo.WinUI.Controls;

internal sealed partial class HighlightPicker : UserControl
{
    private const double StripWidth = 76;
    private const double StripHeight = 6;
    private const double KnobSize = 16;
    private const double KnobHalf = KnobSize / 2;
    private const double Travel = StripWidth - KnobSize;
    private const double DragThreshold = 4;
    private const double ToolbarGap = 4;
    private const int TransitionMs = 167;

    private const float HueEnd = 300f;
    private const double HueSaturation = 0.55;
    private const double HueValue = 0.99;
    private const int HueSteps = 15;

    private readonly UISettings _uiSettings = new();
    private readonly TranslateTransform _leftShift = new();
    private readonly TranslateTransform _rightShift = new();
    private Border _swatch = null!;
    private FrameworkElement _leftGroup = null!;
    private FrameworkElement _rightGroup = null!;

    private uint? _pointerId;
    private Point _pressPoint;
    private bool _dragStarted;
    private double _startPosition;
    private Color _startColor;
    private Color _pending;
    private Storyboard? _transition;

    public HighlightPicker()
    {
        InitializeComponent();
        Knob.Width = KnobSize;
        Knob.Height = KnobSize;
        Strip.Width = StripWidth;
        Strip.Height = StripHeight;
        Canvas.SetTop(Strip, KnobHalf - StripHeight / 2);
        Strip.Background = CreateHueGradient();
    }

    public event EventHandler<Color>? ColorPicked;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public bool IsOpen { get; private set; }

    public void Attach(Border swatch, FrameworkElement leftGroup, FrameworkElement rightGroup)
    {
        _swatch = swatch;
        _leftGroup = leftGroup;
        _rightGroup = rightGroup;
        leftGroup.RenderTransform = _leftShift;
        rightGroup.RenderTransform = _rightShift;
    }

    internal void TrackPress(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_pointerId is null && point.PointerDeviceType == PointerDeviceType.Mouse &&
            point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            _pointerId = e.Pointer.PointerId;
            _pressPoint = point.Position;
            _dragStarted = false;
        }
    }

    internal void TrackMove(PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_dragStarted)
        {
            double dx = point.Position.X - _pressPoint.X;
            double dy = point.Position.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < DragThreshold * DragThreshold)
            {
                return;
            }

            _dragStarted = true;
            OpenAt(point.Position.X);
        }
        else if (IsOpen)
        {
            UpdateFromPointer(point.Position.X);
        }
    }

    internal bool FinishPress(PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return false;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            return _dragStarted;
        }

        _pointerId = null;
        if (IsOpen)
        {
            UpdateFromPointer(point.Position.X);
            Close(apply: true);
        }
        return _dragStarted;
    }

    internal void CancelPress(PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            _pointerId = null;
            Close(apply: false);
        }
    }

    private void OpenAt(double pointerX)
    {
        _transition?.Stop();
        _transition = null;
        _leftShift.X = 0;
        _rightShift.X = 0;

        Rect swatchRect = BoundsOf(_swatch);
        _startColor = ((SolidColorBrush)_swatch.Background).Color;
        _pending = _startColor;
        _startPosition = Math.Clamp(HueOf(_startColor) / (double)HueEnd, 0, 1);

        // 用起始色在色带上的位置反推左端。拖动开始后，两端固定，
        // 指针可以直接在整条色带上取色，不会把色带一起拖走。
        TrackShift.X = pointerX - KnobHalf - _startPosition * Travel;
        TrackShift.Y = swatchRect.Y + swatchRect.Height / 2 - KnobHalf;
        SetKnob(_startPosition);
        double leftOffset = Math.Min(0, TrackShift.X - ToolbarGap - BoundsOf(_leftGroup).Right);
        double rightOffset = Math.Max(0, TrackShift.X + StripWidth + ToolbarGap - BoundsOf(_rightGroup).X);

        _swatch.Opacity = 0;
        Layer.Visibility = Visibility.Visible;
        IsOpen = true;
        Opened?.Invoke(this, EventArgs.Empty);

        StripScale.ScaleX = 1;
        Strip.Opacity = 1;
        _leftShift.X = leftOffset;
        _rightShift.X = rightOffset;
        if (!_uiSettings.AnimationsEnabled)
        {
            return;
        }

        var story = new Storyboard();
        AddAnimation(story, StripScale, "ScaleX", KnobSize / StripWidth, 1);
        AddAnimation(story, Strip, "Opacity", 0, 1);
        AddAnimation(story, _leftShift, "X", 0, leftOffset);
        AddAnimation(story, _rightShift, "X", 0, rightOffset);
        RunTransition(story);
    }

    private void UpdateFromPointer(double pointerX)
    {
        double position = Math.Clamp((pointerX - TrackShift.X - KnobHalf) / Travel, 0, 1);
        _pending = Math.Abs(position - _startPosition) < 0.0001
            ? _startColor
            : FromHue((float)(position * HueEnd));
        SetKnob(position);
    }

    private void SetKnob(double position)
    {
        KnobShift.X = position * Travel;
        Knob.Background = new SolidColorBrush(_pending);

        // 缩放色带时以滑块为支点。即使在展开过程中继续拖，滑块也会对准当前色。
        StripScale.CenterX = KnobShift.X + KnobHalf;
    }

    public void Close(bool apply)
    {
        if (!IsOpen)
        {
            return;
        }
        IsOpen = false;

        if (apply)
        {
            ColorPicked?.Invoke(this, _pending);
        }

        if (!_uiSettings.AnimationsEnabled)
        {
            _transition?.Stop();
            _transition = null;
            FinishClose();
            return;
        }

        Rect swatchRect = BoundsOf(_swatch);
        double homeX = swatchRect.X + swatchRect.Width / 2 - StripScale.CenterX;
        var story = new Storyboard();
        AddAnimation(story, TrackShift, "X", TrackShift.X, homeX);
        AddAnimation(story, StripScale, "ScaleX", StripScale.ScaleX, KnobSize / StripWidth);
        AddAnimation(story, Strip, "Opacity", Strip.Opacity, 0);
        AddAnimation(story, _leftShift, "X", _leftShift.X, 0);
        AddAnimation(story, _rightShift, "X", _rightShift.X, 0);
        RunTransition(story, FinishClose);
    }

    private void FinishClose()
    {
        Layer.Visibility = Visibility.Collapsed;
        _leftShift.X = 0;
        _rightShift.X = 0;
        _swatch.Opacity = 1;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private Rect BoundsOf(FrameworkElement element) => element.TransformToVisual(this)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private LinearGradientBrush CreateHueGradient()
    {
        // 渐变首尾对应滑块中心能到达的两端，扣除色带边框占用的 1 DIP。
        double inset = (KnobHalf - Strip.BorderThickness.Left) /
            (StripWidth - Strip.BorderThickness.Left - Strip.BorderThickness.Right);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(inset, 0),
            EndPoint = new Point(1 - inset, 0),
        };
        for (int i = 0; i <= HueSteps; i++)
        {
            double offset = (double)i / HueSteps;
            brush.GradientStops.Add(new GradientStop
            {
                Offset = offset,
                Color = FromHue((float)(offset * HueEnd)),
            });
        }
        return brush;
    }

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

    private void RunTransition(Storyboard story, Action? onCompleted = null)
    {
        _transition?.Stop();
        _transition = story;
        story.Completed += (_, _) =>
        {
            if (ReferenceEquals(_transition, story))
            {
                onCompleted?.Invoke();
                story.Stop();
                _transition = null;
            }
        };
        story.Begin();
    }

    private static void AddAnimation(Storyboard story, DependencyObject target, string property,
        double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(TransitionMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }
}
