using Microsoft.UI.Xaml;

namespace LumiText.Demo;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new DemoWindow();
        _window.Activate();
    }
}
