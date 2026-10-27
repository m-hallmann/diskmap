using System.Diagnostics;
using System.Text.RegularExpressions;
using DiskMap.Core.Games;
using DiskMap.Core.Model;
using DiskMap.Core.Native;

namespace DiskMap.Core.Actions;

public enum SuggestionKind
{
    /// <summary>Delete the contents of the listed folders (the folders themselves stay).</summary>
    EmptyFolders,
    /// <summary>Run a Windows tool.</summary>
    Command,
    /// <summary>Open the map, filtered, so the user can pick what to delete.</summary>
    Review,
    /// <summary>Open a Windows settings page.</summary>
    OpenSettings,
}

public sealed class Suggestion
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Category Category { get; init; }
    public required SuggestionKind Kind { get; init; }
    public required string ActionLabel { get; init; }
    public long Bytes { get; init; }
    public bool SizeIsEstimate { get; init; }
    public bool NeedsAdmin { get; init; }
    public IReadOnlyList<DiskNode> Nodes { get; init; } = [];
    public string ConfirmTitle { get; init; } = "";
    public string ConfirmBody { get; init; } = "";
    public string DoneText { get; init; } = "";
    /// <summary>For <see cref="SuggestionKind.Review"/>: the folder to open and the search to apply.</summary>
    public DiskNode? ReviewFolder { get; init; }
    public string? ReviewQuery { get; init; }
    public Category? ReviewCategory { get; init; }
    public string? SettingsUri { get; init; }
    public Func<CancellationToken, Task<string?>>? Run { get; init; }
}

/// <summary>Finds safe ways to free space on a scanned drive.</summary>
public static class CleanupService
{
    public static List<Suggestion> Analyze(ScanResult scan, IEnumerable<string>? steamLibraries = null)
    {
        var root = scan.Root;
        string drive = scan.DriveRoot;
        bool systemDrive = string.Equals(Path.GetPathRoot(Environment.SystemDirectory), drive, StringComparison.OrdinalIgnoreCase);
        var list = new List<Suggestion>();

        if (root.Child("$Recycle.Bin") is { Size: > 0 } bin)
        {
            list.Add(new Suggestion
            {
                Key = "recycle", Title = "Recycle Bin", Category = Category.Caches, Kind = SuggestionKind.Command,
                Description = $"Deleted files still use space on {Letter(drive)} until the bin is emptied.",
                ActionLabel = "Empty", Bytes = bin.Size, Nodes = [bin],
                ConfirmTitle = "Empty the Recycle Bin?", ConfirmBody = $"Everything in the Recycle Bin on {Letter(drive)} is deleted for good.",
                DoneText = "Recycle Bin emptied",
                Run = _ =>
                {
                    Shell32.SHEmptyRecycleBin(0, drive, Shell32.SHERB_NOCONFIRMATION | Shell32.SHERB_NOPROGRESSUI | Shell32.SHERB_NOSOUND);
                    return Task.FromResult<string?>(null);
                },
            });
        }

        if (systemDrive && root.Child("hiberfil.sys") is { Size: > 0 } hiber)
        {
            list.Add(new Suggestion
            {
                Key = "hibernate", Title = "Turn off hibernation", Category = Category.System, Kind = SuggestionKind.Command,
                Description = "Removes hiberfil.sys with powercfg. Fast Startup is turned off too.",
                ActionLabel = "Turn off", Bytes = hiber.Size, NeedsAdmin = true, Nodes = [hiber],
                ConfirmTitle = "Turn off hibernation?", ConfirmBody = "Runs powercfg /hibernate off. This removes hiberfil.sys and also turns off Fast Startup.",
                DoneText = "Hibernation turned off",
                Run = async ct => Check(await ShellLauncher.RunAsync("powercfg.exe", "/hibernate off", ct)),
            });
        }

        var temps = new List<DiskNode>();
        foreach (var users in root.Child("Users")?.Children ?? [])
        {
            if (users.Find(@"AppData\Local\Temp") is { Size: > 0 } t) temps.Add(t);
        }
        if (root.Find(@"Windows\Temp") is { Size: > 0 } wt) temps.Add(wt);
        AddFolders(list, "temp", "Temporary files", "Temp folders. Files that are in use are skipped.", Category.Caches, temps,
            "Delete temporary files?", "Deletes everything in the Temp folders. Files that are in use are skipped.", "Temporary files deleted");

        var caches = new List<DiskNode>();
        foreach (var user in root.Child("Users")?.Children ?? [])
        {
            foreach (var rel in new[]
                     {
                         @"AppData\Local\NVIDIA\DXCache", @"AppData\Local\NVIDIA\GLCache", @"AppData\Local\D3DSCache",
                         @"AppData\Local\AMD\DxCache", @"AppData\Local\AMD\DxcCache", @"AppData\Local\Unity\cache",
                         @"AppData\Local\npm-cache", @"AppData\Local\pip\cache", @"AppData\Local\Microsoft\Windows\INetCache",
                         @"AppData\Local\CrashDumps",
                     })
            {
                if (user.Find(rel) is { Size: > 0 } c) caches.Add(c);
            }
        }
        var downloads = new List<DiskNode>();
        foreach (var lib in steamLibraries ?? GameCatalog.SteamLibraries())
        {
            if (!lib.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) continue;
            var steamapps = root.Find(Path.Combine(lib[drive.Length..], "steamapps"));
            if (steamapps?.Child("shadercache") is { Size: > 0 } sc) caches.Add(sc);
            if (steamapps?.Child("downloading") is { Size: > 0 } dl) downloads.Add(dl);
            if (steamapps?.Child("temp") is { Size: > 0 } st) downloads.Add(st);
        }
        AddFolders(list, "caches", "App and shader caches", "Graphics driver, Steam shader, Unity, npm and pip caches. Rebuilt when needed.",
            Category.Caches, caches, "Delete app and shader caches?",
            "Apps rebuild these caches when they need them, so some first launches will be slower.", "Caches deleted");
        AddFolders(list, "steam-downloads", "Unfinished Steam downloads", "Leftovers from game updates that never finished.",
            Category.Caches, downloads, "Delete unfinished Steam downloads?",
            "Steam downloads whatever it still needs the next time it updates. Close Steam first.", "Unfinished downloads deleted");

        if (root.Child("System Volume Information") is { Size: > 1L << 30 } svi && Elevation.IsElevated)
        {
            list.Add(new Suggestion
            {
                Key = "restore", Title = "Older restore points", Category = Category.System, Kind = SuggestionKind.Command,
                Description = "Keeps the most recent restore point. Uses vssadmin.", ActionLabel = "Remove", Bytes = svi.Size,
                SizeIsEstimate = true, NeedsAdmin = true, Nodes = [],
                ConfirmTitle = "Remove older restore points?", ConfirmBody = "Keeps your most recent restore point and deletes the older ones.",
                DoneText = "Older restore points removed",
                Run = ct => RemoveOldShadowCopies(drive, ct),
            });
        }

        if (systemDrive && root.Find(@"Windows\WinSxS") is { } sxs)
        {
            list.Add(new Suggestion
            {
                Key = "winsxs", Title = "Windows component cleanup", Category = Category.System, Kind = SuggestionKind.Command,
                Description = "Removes superseded updates from WinSxS. Uses DISM and can take a few minutes.",
                ActionLabel = "Run cleanup", Bytes = sxs.Size / 10, SizeIsEstimate = true, NeedsAdmin = true, Nodes = [],
                ConfirmTitle = "Clean up Windows components?",
                ConfirmBody = "Runs DISM /Online /Cleanup-Image /StartComponentCleanup. This can take a few minutes.",
                DoneText = "Component cleanup finished",
                Run = async ct => Check(await ShellLauncher.RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", ct)),
            });
        }

        foreach (var user in root.Child("Users")?.Children ?? [])
        {
            if (user.Child("Downloads") is not { } dls || dls.CategorySizes == null) continue;
            long archives = dls.CategorySizes[(int)Category.Archives];
            if (archives < 256L << 20) continue;
            list.Add(new Suggestion
            {
                Key = "downloads-" + user.Name, Title = "Installers and archives in Downloads",
                Description = $"Setup files, archives and disk images in {user.Name}\\Downloads. Pick what to delete.",
                Category = Category.Archives, Kind = SuggestionKind.Review, ActionLabel = "Review", Bytes = archives,
                ReviewFolder = dls, ReviewCategory = Category.Archives,
            });
        }

        if (root.Child("Windows.old") is { Size: > 0 } old)
        {
            list.Add(new Suggestion
            {
                Key = "windows-old", Title = "Previous Windows installation", Category = Category.System, Kind = SuggestionKind.OpenSettings,
                Description = "Windows.old can only be removed with Windows' own cleanup. Opens Storage settings.",
                ActionLabel = "Open settings", Bytes = old.Size, SettingsUri = "ms-settings:storagepolicies",
            });
        }

        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    private static void AddFolders(List<Suggestion> list, string key, string title, string description, Category cat,
        List<DiskNode> folders, string confirmTitle, string confirmBody, string done)
    {
        long bytes = folders.Sum(f => f.Size);
        if (bytes < 64L << 20) return;
        list.Add(new Suggestion
        {
            Key = key, Title = title, Description = description, Category = cat, Kind = SuggestionKind.EmptyFolders,
            ActionLabel = "Delete", Bytes = bytes, Nodes = folders,
            ConfirmTitle = confirmTitle, ConfirmBody = confirmBody, DoneText = done,
        });
    }

    private static string Letter(string drive) => drive.TrimEnd('\\');

    private static string? Check((int ExitCode, string Output) r) =>
        r.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(r.Output) ? $"The tool exited with code {r.ExitCode}." : r.Output.Trim());

    private static async Task<string?> RemoveOldShadowCopies(string drive, CancellationToken ct)
    {
        var (code, output) = await ShellLauncher.RunAsync("vssadmin.exe", $"list shadows /for={Letter(drive)}", ct);
        if (code != 0) return output.Trim();
        // vssadmin prints one "Shadow Copy ID: {guid}" line per restore point (the label itself is localized,
        // so count the GUIDs that follow a colon).
        int shadows = Regex.Matches(output, @":\s*\{[0-9a-fA-F\-]{36}\}\s*$", RegexOptions.Multiline).Count;
        for (int i = 0; i < shadows - 1; i++)
        {
            var r = await ShellLauncher.RunAsync("vssadmin.exe", $"delete shadows /for={Letter(drive)} /oldest /quiet", ct);
            if (r.ExitCode != 0) return r.Output.Trim();
        }
        return null;
    }

    /// <summary>Deletes the contents of folders, keeping the folders themselves. Returns the paths it could not delete.</summary>
    public static async Task<DeleteReport> EmptyFoldersAsync(IEnumerable<DiskNode> folders, CancellationToken ct)
    {
        var targets = new List<string>();
        foreach (var f in folders)
        {
            var path = f.FullPath;
            if (!Directory.Exists(path)) continue;
            try
            {
                targets.AddRange(Directory.EnumerateFileSystemEntries(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(ex.Message);
            }
        }
        return await DeleteService.DeleteAsync(targets, permanently: true, progress: null, ct);
    }
}
