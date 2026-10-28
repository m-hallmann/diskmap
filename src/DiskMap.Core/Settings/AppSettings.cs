using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core.Settings;

public enum ThresholdUnit { Gigabytes, Percent }

public enum RemindPolicy { AfterFiveGbOrDaily, AfterTenGb, Daily, Once }

public enum LockedFilePolicy { Ask, DeleteAtRestart, Skip }

/// <summary>What the colours in the map stand for.</summary>
public enum MapColoring { Folder, FileType }

public sealed class DriveAlert
{
    public string Drive { get; set; } = "C:\\";
    public bool Enabled { get; set; } = true;
    public double Threshold { get; set; } = 50;
    public ThresholdUnit Unit { get; set; } = ThresholdUnit.Gigabytes;

    public long ThresholdBytes(long totalBytes) => Unit == ThresholdUnit.Percent
        ? (long)(totalBytes * Threshold / 100)
        : (long)(Threshold * (1L << 30));

    public string Describe() => Unit == ThresholdUnit.Percent ? $"{Threshold:0.#}%" : $"{Threshold:0.#} GB";
}

/// <summary>Settings shared by the app and the tray helper (stored as JSON in %LOCALAPPDATA%\diskmap).</summary>
public sealed class AppSettings
{
    public List<DriveAlert> Alerts { get; set; } = new();
    public int CheckIntervalMinutes { get; set; } = 5;
    public RemindPolicy Remind { get; set; } = RemindPolicy.AfterFiveGbOrDaily;
    public bool StartWithWindows { get; set; } = true;
    public bool DeletePermanently { get; set; } = true;
    public LockedFilePolicy LockedFiles { get; set; } = LockedFilePolicy.Ask;
    public int HistoryDays { get; set; } = 90;
    public string Theme { get; set; } = "System";
    public MapColoring MapColors { get; set; } = MapColoring.Folder;
    public Layout.MapTiling MapTiling { get; set; } = Layout.MapTiling.Rows;
    public bool AutoUpdate { get; set; } = true;

    public DriveAlert AlertFor(string drive, long totalBytes)
    {
        var alert = Alerts.FirstOrDefault(a => string.Equals(a.Drive, drive, StringComparison.OrdinalIgnoreCase));
        if (alert != null) return alert;
        // Default: 10% of the drive, between 5 and 50 GB.
        double gb = Math.Clamp(totalBytes / (double)(1L << 30) * 0.10, 5, 50);
        alert = new DriveAlert { Drive = drive, Threshold = Math.Round(gb), Unit = ThresholdUnit.Gigabytes };
        Alerts.Add(alert);
        return alert;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), SettingsJson.Default.AppSettings) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

public static class AppPaths
{
    /// <summary>
    /// Installed and portable copies keep their data in a "data" folder next to the app
    /// (Velopack layout: &lt;root&gt;\current\DiskMap.exe, &lt;root&gt;\Update.exe), so uninstalling or
    /// deleting a portable folder removes everything. Builds from source use %LOCALAPPDATA%\diskmap-dev.
    /// </summary>
    public static string DataFolder { get; } = ResolveDataFolder();

    /// <summary>The Velopack root folder when running from an installed or portable copy, else null.</summary>
    public static string? InstallRoot { get; } = ResolveInstallRoot();

    private static string? ResolveInstallRoot()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!string.Equals(Path.GetFileName(baseDir), "current", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(baseDir);
        return root != null && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
    }

    private static string ResolveDataFolder()
    {
        var root = ResolveInstallRoot();
        return root != null
            ? Path.Combine(root, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "diskmap-dev");
    }

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string HistoryFolder => Path.Combine(DataFolder, "history");
    public static string FreeSpaceFile => Path.Combine(DataFolder, "freespace.json");
    public static string TrayStateFile => Path.Combine(DataFolder, "tray-state.json");

    public static string DriveKey(string driveRoot) => driveRoot.TrimEnd('\\', ':').ToUpperInvariant();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(History.FreeSpaceLog))]
[JsonSerializable(typeof(History.Snapshot))]
[JsonSerializable(typeof(TrayState))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>Per-drive alert bookkeeping for the tray helper.</summary>
public sealed class TrayState
{
    public Dictionary<string, DriveAlertState> Drives { get; set; } = new();
    public DateTime? SnoozedUntil { get; set; }

    public static TrayState Load()
    {
        try
        {
            if (File.Exists(AppPaths.TrayStateFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.TrayStateFile), SettingsJson.Default.TrayState) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new TrayState();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(AppPaths.TrayStateFile, JsonSerializer.Serialize(this, SettingsJson.Default.TrayState));
    }
}

public sealed class DriveAlertState
{
    public bool Below { get; set; }
    public long LastAlertFree { get; set; }
    public DateTime? LastAlertAt { get; set; }
}
