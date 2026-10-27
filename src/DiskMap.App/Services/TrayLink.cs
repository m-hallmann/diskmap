using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskMap.Core.Actions;
using DiskMap.Core.Native;
using Microsoft.Win32;

namespace DiskMap.Services;

/// <summary>Starts, registers and talks to the tray helper (DiskMap.Tray.exe).</summary>
internal static partial class TrayLink
{
    public const string WindowClass = "diskmap.tray";
    private const uint WM_APP = 0x8000;
    public const uint MsgTestAlert = WM_APP + 2;
    public const uint MsgReloadSettings = WM_APP + 3;
    private const string TaskName = @"diskmap\Tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void RememberAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\diskmap");
            key.SetValue("AppPath", Environment.ProcessPath ?? "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }

    public static string? FindTrayExe()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "DiskMap.Tray.exe");
        if (File.Exists(sibling)) return sibling;

        // Development layout: src\DiskMap.App\bin\...  ->  src\DiskMap.Tray\bin\...
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var trayProject = Path.Combine(dir.FullName, "DiskMap.Tray");
            if (!Directory.Exists(trayProject)) continue;
            var bin = Path.Combine(trayProject, "bin");
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateFiles(bin, "DiskMap.Tray.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    public static bool IsRunning => FindWindow(WindowClass, null) != 0;

    public static void EnsureRunning()
    {
        if (IsRunning) return;
        var exe = FindTrayExe();
        if (exe == null)
        {
            Log.Write("Tray helper not found.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write("Could not start tray helper: " + ex.Message);
        }
    }

    public static void Stop()
    {
        var hwnd = FindWindow(WindowClass, null);
        if (hwnd != 0) PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
    }

    public static void Post(uint message)
    {
        var hwnd = FindWindow(WindowClass, null);
        if (hwnd != 0) PostMessage(hwnd, message, 0, 0);
    }

    /// <summary>
    /// Starts the tray helper at sign-in. With admin rights this is a scheduled task that runs with
    /// highest privileges, so diskmap can open from the tray with admin rights and no UAC prompt.
    /// </summary>
    public static async Task SetAutostartAsync(bool enabled)
    {
        var exe = FindTrayExe();
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && exe != null && !Elevation.IsElevated) run.SetValue("diskmap", $"\"{exe}\"");
            else run.DeleteValue("diskmap", false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

        if (!Elevation.IsElevated) return;
        if (enabled && exe != null)
        {
            var r = await ShellLauncher.RunAsync("schtasks.exe",
                $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F", CancellationToken.None);
            if (r.ExitCode != 0) Log.Write("schtasks /Create failed: " + r.Output);
        }
        else
        {
            await ShellLauncher.RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", CancellationToken.None);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
