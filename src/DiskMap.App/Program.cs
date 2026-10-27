using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DiskMap;

public static partial class Program
{
    public const string InstanceKey = "diskmap-main";

    /// <summary>Started with --demo: shows made-up data, saves nothing, starts no tray helper.</summary>
    public static bool IsDemo { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // Must run first: handles install, update and uninstall events and may exit right away.
        Velopack.VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => Services.InstallHooks.BeforeUpdate())
            .OnBeforeUninstallFastCallback(_ => Services.InstallHooks.BeforeUninstall())
            .Run();

        IsDemo = args.Contains("--demo");
        if (IsDemo)
        {
            // Screenshots shouldn't reveal the language or regional format of the PC they were taken on.
            var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
            System.Globalization.CultureInfo.CurrentCulture = english;
            System.Globalization.CultureInfo.CurrentUICulture = english;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance()) return 0;

        // The UI is English; keep the controls' built-in text (toggle "On/Off", tooltips) English too.
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US"; }
        catch (Exception) { }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Only one diskmap window: a second launch (e.g. from the tray) activates the first one.</summary>
    /// <summary>Debug builds can run a scripted self-test next to a normal instance.</summary>
    public static bool IsSelfTest =>
#if DEBUG
        Environment.GetEnvironmentVariable("DISKMAP_SELFTEST") == "1";
#else
        false;
#endif

    private static bool RedirectToRunningInstance()
    {
        if (IsSelfTest || IsDemo) return false;
        AppInstance main;
        try
        {
            main = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (COMException)
        {
            return false;
        }
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.Current?.OnRedirectedActivation(e);
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow(main.ProcessId);
        var done = CreateEvent(0, true, false, null);
        _ = Task.Run(async () =>
        {
            try { await main.RedirectActivationToAsync(activation); }
            catch (COMException) { }
            SetEvent(done);
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, nint[] handles, out uint index);
}
