using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace LumiMemo.WinUI.Controls;

internal sealed class ToolbarPickerAnimation
{
    private const int TransitionMs = 167;
    private readonly UISettings _uiSettings = new();
    private Storyboard? _story;

    public bool Enabled => _uiSettings.AnimationsEnabled;

    public void Stop()
    {
        _story?.Stop();
        _story = null;
    }

    public void Run(Storyboard story, Action? onCompleted = null)
    {
        Stop();
        _story = story;
        story.Completed += (_, _) =>
        {
            if (ReferenceEquals(_story, story))
            {
                onCompleted?.Invoke();
                Stop();
            }
        };
        story.Begin();
    }

    public static void Add(Storyboard story, DependencyObject target, string property,
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
