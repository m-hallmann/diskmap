using System.Diagnostics;

namespace DiskMap.Core.Actions;

/// <summary>
/// Opens things through the already running (non-admin) Explorer, so files, folders and launcher
/// links don't inherit diskmap's admin rights.
/// </summary>
public static class ShellLauncher
{
    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Start("explorer.exe", $"/select,\"{path}\"");
        else
            Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
    }

    public static void Open(string pathOrUri) => Start("explorer.exe", $"\"{pathOrUri}\"");

    public static void Start(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Runs a command line hidden and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await stdout) + (await stderr));
    }
}
