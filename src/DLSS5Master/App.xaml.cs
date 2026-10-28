using Microsoft.UI.Xaml;
using DLSS5Master.Core;

namespace DLSS5Master;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppPaths.Log("Unhandled UI exception: " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppPaths.Log("Unobserved task: " + e.Exception); e.SetObserved(); };
    }

    // One DLSS 5 Master per Windows user: a second start wakes the first one's window and quits.
    private static Mutex? _single;
    private static EventWaitHandle? _showRequest;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private const int AsfwAny = -1;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A copy started on a test data folder (DLSS5MASTER_DATA) is its own instance, so checks can run next to the real app.
        var scope = Environment.GetEnvironmentVariable("DLSS5MASTER_DATA") is { Length: > 0 }
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToUpperInvariant())))[..12]
            : "";
        _single = new Mutex(true, @"Local\DLSS5Master.SingleInstance" + scope, out bool first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DLSS5Master.ShowWindow" + scope);
        if (!first)
        {
            // This start came from the user, so it may hand the foreground over to the running copy.
            AllowSetForegroundWindow(AsfwAny);
            _showRequest.Set();
            Exit();
            return;
        }
        try
        {
            Window = new MainWindow();
            Window.Activate();
            var ui = Window.DispatcherQueue;
            var show = _showRequest;
            new Thread(() => { while (show.WaitOne()) ui.TryEnqueue(() => Window?.BringToFront()); })
                { IsBackground = true, Name = "Show window on second start" }.Start();
        }
        catch (Exception e)
        {
            AppPaths.Log("Startup failed: " + e);
            throw;
        }
    }
}
