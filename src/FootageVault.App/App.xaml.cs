using FootageVault.Core;
using FootageVault.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FootageVault;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    /// <summary>Window handle, needed by Windows pickers.</summary>
    public static IntPtr Hwnd => Window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI error", e.Exception);
            e.Handled = true;      // keep the app (and running uploads) alive
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task error", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppHost.Current.Init(DispatcherQueue.GetForCurrentThread());
        Window = new MainWindow();
        Window.Activate();

        // Used by the automatic Windows check: --page <name> and --scan <folder>... (no effect otherwise)
        var argv = Environment.GetCommandLineArgs();
        var scan = argv.SkipWhile(a => a != "--scan").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
        var page = argv.SkipWhile(a => a != "--page").Skip(1).FirstOrDefault();
        if (scan.Count > 0) Window.StartScan(scan);
        else if (page is not null) Window.GoTo(page);
    }
}
