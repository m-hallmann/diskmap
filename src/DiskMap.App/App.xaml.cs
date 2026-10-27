using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DiskMap;

public partial class App : Application
{
    public App()
    {
        // Set the app-wide theme before any resources load, so popups, dialogs and anything that looks
        // brushes up in code match the window. (The window can still switch theme later from Settings.)
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--theme");
        string? theme = i >= 0 && i + 1 < args.Length ? args[i + 1] : Program.IsDemo ? null : Core.Settings.AppSettings.Load().Theme;
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Dark;
        else if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Services.Log.Write("Unhandled: " + e.Exception);
            e.Handled = true;
        };
    }

    public static new App? Current => Application.Current as App;

    public MainWindow? Window { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Window.Activate();
    }

    public void OnRedirectedActivation(AppActivationArguments e)
    {
        string[] args = [];
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            args = (launch.Arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        Window?.DispatcherQueue.TryEnqueue(() => Window.BringToFront(args));
    }
}
