using Microsoft.UI.Xaml;

namespace SerialScope;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        Program.Log("App constructor enter");
        InitializeComponent();
        Program.Log("App InitializeComponent complete");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Program.Log("OnLaunched enter");
            _window = new MainWindow();
            Program.Log("MainWindow created");
            _window.Activate();
            Program.Log("Window activated");
        }
        catch (Exception ex)
        {
            Program.Log("OnLaunched exception: " + ex);
            throw;
        }
    }
}
