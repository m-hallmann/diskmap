using DiskMap.Core.Actions;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace DiskMap.Services;

/// <summary>Updates from GitHub Releases (Velopack). Does nothing in builds that weren't installed or unzipped from a release.</summary>
internal static class Updater
{
    public const string RepoUrl = "https://github.com/m-hallmann/diskmap";

    private static readonly Lazy<UpdateManager?> s_manager = new(() =>
    {
        try { return new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch (Exception ex) { Log.Write("Updater unavailable: " + ex.Message); return null; }
    });

    public static bool IsInstalled => s_manager.Value?.IsInstalled == true;
    public static bool IsPortable => s_manager.Value?.IsPortable == true;
    public static string? CurrentVersion => s_manager.Value?.CurrentVersion?.ToString();
    public static UpdateInfo? Pending { get; private set; }

    public static string Describe()
    {
        var version = CurrentVersion ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        string kind = !IsInstalled ? "built from source" : IsPortable ? "portable" : "installed";
        return $"diskmap {version} ({kind})";
    }

    /// <summary>Checks GitHub and downloads a newer release in the background. Returns the new version, if any.</summary>
    public static async Task<string?> CheckAndDownloadAsync()
    {
        var mgr = s_manager.Value;
        if (mgr == null || !mgr.IsInstalled) return null;
        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return null;
            await mgr.DownloadUpdatesAsync(info);
            Pending = info;
            return info.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            return null;
        }
    }

    public static void ApplyAndRestart()
    {
        var mgr = s_manager.Value;
        if (mgr == null || Pending == null) return;
        TrayLink.Stop();
        mgr.ApplyUpdatesAndRestart(Pending.TargetFullRelease);
    }
}

/// <summary>Velopack hooks: tidy up what diskmap registered outside its own folder.</summary>
internal static class InstallHooks
{
    public static void BeforeUpdate() => TrayLink.Stop();

    public static void BeforeUninstall()
    {
        TrayLink.Stop();
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                run?.DeleteValue("diskmap", false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\diskmap", false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

        // The sign-in task was created with admin rights, so removing it may need them too.
        var r = ShellLauncher.RunAsync("schtasks.exe", @"/Delete /TN ""diskmap\Tray"" /F", CancellationToken.None).GetAwaiter().GetResult();
        if (r.ExitCode != 0 && ShellLauncher.RunAsync("schtasks.exe", @"/Query /TN ""diskmap\Tray""", CancellationToken.None).GetAwaiter().GetResult().ExitCode == 0)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", @"/Delete /TN ""diskmap\Tray"" /F")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
                p?.WaitForExit(30_000);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
