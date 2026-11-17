using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace LumiMemo.WinUI.Controls;

internal sealed partial class HeadingPicker : UserControl
{
    private const double ChoiceSize = 20;
    private const double ToolbarGap = 4;

    private readonly ToolbarPickerAnimation _animation = new();
    private readonly (Grid Cell, Border Outline, TranslateTransform Shift)[] _choices;
    private readonly TranslateTransform _leftShift = new();
    private readonly TranslateTransform _dividerShift = new();
    private readonly TranslateTransform _rightShift = new();
    private FrameworkElement _face = null!;
    private FrameworkElement _divider = null!;
    private FrameworkElement _status = null!;

    private double _startX;
    private double _stripLeft;
    private int _startLevel;
    private int _previewLevel;

    public HeadingPicker()
    {
        InitializeComponent();
        _choices = [(Choice1, Outline1, new()), (Choice2, Outline2, new()), (Choice3, Outline3, new())];
        foreach (var (cell, _, shift) in _choices)
        {
            cell.Width = ChoiceSize;
            cell.Height = ChoiceSize;
            cell.RenderTransform = shift;
        }
    }

    public event Action<int>? LevelPicked;
    public event Action<bool>? VisibilityChanged;
    public bool IsOpen { get; private set; }

    public void Attach(FrameworkElement face, FrameworkElement leftGroup,
        FrameworkElement divider, FrameworkElement status)
    {
        _face = face;
        _divider = divider;
        _status = status;
        leftGroup.RenderTransform = _leftShift;
        divider.RenderTransform = _dividerShift;
        status.RenderTransform = _rightShift;
    }

    public void Open(double pointerX, int currentLevel)
    {
        Dismiss();

        _startX = pointerX;
        _startLevel = currentLevel;
        SetPreviewLevel(currentLevel);

        double width = ChoiceSize * _choices.Length;
        Rect faceBounds = BoundsOf(_face);
        double homeX = faceBounds.X + faceBounds.Width / 2 - ChoiceCenter(currentLevel);
        _stripLeft = Math.Clamp(homeX, 0, Math.Max(0, ActualWidth - width));
        StripShift.X = _stripLeft;
        StripShift.Y = FaceTopForStrip(faceBounds);
        double leftOffset = Math.Min(0, StripShift.X - ToolbarGap - BoundsOf(_divider).Right);
        double rightOffset = Math.Max(0, StripShift.X + width + ToolbarGap - BoundsOf(_status).X);

        IsOpen = true;
        Layer.Visibility = Visibility.Visible;
        VisibilityChanged?.Invoke(true);
        _leftShift.X = leftOffset;
        _dividerShift.X = leftOffset;
        _rightShift.X = rightOffset;

        if (!_animation.Enabled)
        {
            return;
        }

        var story = new Storyboard();
        ToolbarPickerAnimation.Add(story, StripShift, "X", homeX, _stripLeft);
        ToolbarPickerAnimation.Add(story, _leftShift, "X", 0, leftOffset);
        ToolbarPickerAnimation.Add(story, _dividerShift, "X", 0, leftOffset);
        ToolbarPickerAnimation.Add(story, _rightShift, "X", 0, rightOffset);
        AnimateChoices(story, currentLevel, expanding: true);
        _animation.Run(story);
    }

    public void UpdateFromPointer(double pointerX)
    {
        if (!IsOpen)
        {
            return;
        }

        // 从按住时的档位开始，根据指针到选项两端的距离切换档位。
        double width = ChoiceSize * _choices.Length;
        double center = ChoiceCenter(_startLevel);
        double delta = pointerX - _startX;
        double physicalSpan = delta < 0 ? _startX - _stripLeft : _stripLeft + width - _startX;
        double choiceSpan = delta < 0 ? center : width - center;
        double x = center + delta * choiceSpan / Math.Max(1, physicalSpan);
        int level = (int)Math.Clamp(Math.Floor(x / ChoiceSize) + 1, 1, _choices.Length);
        if (level != _previewLevel)
        {
            SetPreviewLevel(level);
        }
    }

    private void SetPreviewLevel(int level)
    {
        _previewLevel = level;
        for (int i = 0; i < _choices.Length; i++)
        {
            _choices[i].Outline.Opacity = i + 1 == level ? 1 : 0;
        }
    }

    public void Commit(double pointerX)
    {
        if (!IsOpen)
        {
            return;
        }

        UpdateFromPointer(pointerX);
        LevelPicked?.Invoke(_previewLevel);
        Close();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        IsOpen = false;
        if (!_animation.Enabled)
        {
            Dismiss();
            return;
        }

        Rect faceBounds = BoundsOf(_face);
        double homeX = faceBounds.X + faceBounds.Width / 2 - ChoiceCenter(_previewLevel);
        var story = new Storyboard();
        ToolbarPickerAnimation.Add(story, StripShift, "X", StripShift.X, homeX);
        ToolbarPickerAnimation.Add(story, StripShift, "Y", StripShift.Y, FaceTopForStrip(faceBounds));
        ToolbarPickerAnimation.Add(story, _leftShift, "X", _leftShift.X, 0);
        ToolbarPickerAnimation.Add(story, _dividerShift, "X", _dividerShift.X, 0);
        ToolbarPickerAnimation.Add(story, _rightShift, "X", _rightShift.X, 0);
        AnimateChoices(story, _previewLevel, expanding: false);
        _animation.Run(story, FinishClose);
    }

    public void Dismiss()
    {
        if (Layer.Visibility == Visibility.Visible)
        {
            IsOpen = false;
            _animation.Stop();
            FinishClose();
        }
    }

    private void FinishClose()
    {
        Layer.Visibility = Visibility.Collapsed;
        _leftShift.X = 0;
        _dividerShift.X = 0;
        _rightShift.X = 0;
        VisibilityChanged?.Invoke(false);
    }

    private void AnimateChoices(Storyboard story, int pivotLevel, bool expanding)
    {
        for (int i = 0; i < _choices.Length; i++)
        {
            var (cell, _, shift) = _choices[i];
            double offset = ChoiceCenter(pivotLevel) - ChoiceCenter(i + 1);
            if (expanding)
            {
                ToolbarPickerAnimation.Add(story, shift, "X", offset, 0);
                if (i + 1 != pivotLevel)
                {
                    ToolbarPickerAnimation.Add(story, cell, "Opacity", 0, 1);
                }
            }
            else
            {
                ToolbarPickerAnimation.Add(story, shift, "X", shift.X, offset);
                ToolbarPickerAnimation.Add(story, cell, "Opacity", cell.Opacity, i + 1 == pivotLevel ? 1 : 0);
            }
        }
    }

    private static double ChoiceCenter(int level) => (level - 0.5) * ChoiceSize;

    private static double FaceTopForStrip(Rect face) => face.Y + (face.Height - ChoiceSize) / 2;

    private Rect BoundsOf(FrameworkElement element) => element.TransformToVisual(this)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
}
