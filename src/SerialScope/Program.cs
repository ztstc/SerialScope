using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SerialScope;

public static class Program
{
    private static readonly bool LoggingEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SERIALSCOPE_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "startup.log");

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Log("UNOBSERVED: " + e.Exception);
        Log("=== SerialScope startup ===");
        try
        {
            Log("Initializing COM wrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("Starting XAML application");
            Application.Start(p =>
            {
                try
                {
                    Log("XAML callback");
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    var app = new App();
                    Log("App constructed");
                    GC.KeepAlive(app);
                }
                catch (Exception ex)
                {
                    Log("CALLBACK EXCEPTION: " + ex);
                    throw;
                }
            });
            Log("Application.Start returned");
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            Environment.ExitCode = 1;
        }
    }

    public static void Log(string message)
    {
        Debug.WriteLine(message);
        if (!LoggingEnabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
