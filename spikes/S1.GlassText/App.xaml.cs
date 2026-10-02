using Microsoft.UI.Xaml;

namespace S1.GlassText;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new GlassTextWindow();
        _window.Activate();
    }
}
