using AxfsExplorer.Helpers;
using Microsoft.UI.Xaml;

namespace AxfsExplorer;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ToastHelper.Initialize();
        _window = new MainWindow();
        _window.Activate();
    }
}
