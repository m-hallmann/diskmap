using System.IO.Compression;
using System.Text.Json;
using DiskMap.Core.Model;
using DiskMap.Core.Settings;

namespace DiskMap.Core.History;

/// <summary>Folder totals from one scan. Only folders big enough to matter are stored.</summary>
public sealed class Snapshot
{
    public DateTime TakenAt { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>Relative folder path (no drive) to size on disk.</summary>
    public Dictionary<string, long> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const long MinFolderBytes = 32L << 20;
    public const int MaxDepth = 12;

    public static Snapshot From(ScanResult scan)
    {
        var s = new Snapshot { TakenAt = scan.ScannedAt, UsedBytes = scan.UsedBytes, FreeBytes = scan.FreeBytes };
        void Walk(DiskNode n, string path, int depth)
        {
            if (n.Children == null || depth > MaxDepth) return;
            foreach (var c in n.Children)
            {
                if (!c.IsDirectory || c.Size < MinFolderBytes) continue;
                var p = c.IsSynthetic ? path + "\\<" + c.Name + ">" : path + "\\" + c.Name;
                s.Folders[p] = c.Size;
                Walk(c, p, depth + 1);
            }
        }
        Walk(scan.Root, "", 0);
        return s;
    }
}

public static class SnapshotStore
{
    private static string Folder(string drive) => Path.Combine(AppPaths.HistoryFolder, AppPaths.DriveKey(drive));

    public static void Save(string drive, Snapshot snapshot, int keepDays)
    {
        var dir = Folder(drive);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, snapshot.TakenAt.ToString("yyyyMMdd-HHmmss") + ".json.gz");
        using (var fs = File.Create(file))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gz, snapshot, SettingsJson.Default.Snapshot);
        }

        // Keep at most one snapshot per day once they are older than a day, and none beyond keepDays.
        var all = List(drive);
        var seenDays = new HashSet<DateTime>();
        foreach (var (taken, path) in all.OrderByDescending(x => x.TakenAt))
        {
            bool tooOld = taken < DateTime.Now.AddDays(-keepDays);
            bool duplicateDay = taken < DateTime.Now.AddDays(-1) && !seenDays.Add(taken.Date);
            if (tooOld || duplicateDay) TryDelete(path);
        }
    }

    public static List<(DateTime TakenAt, string Path)> List(string drive)
    {
        var dir = Folder(drive);
        var result = new List<(DateTime, string)>();
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json.gz"))
        {
            var stamp = Path.GetFileName(f)[..15];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)) result.Add((t, f));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>
    /// Picks what to compare a new scan with: the newest snapshot at least a day old,
    /// otherwise the oldest one there is (but never one from the last few minutes).
    /// </summary>
    public static Snapshot? LoadBaseline(string drive, DateTime now)
    {
        var all = List(drive).Where(x => x.TakenAt < now.AddMinutes(-10)).ToList();
        if (all.Count == 0) return null;
        var pick = all.LastOrDefault(x => x.TakenAt <= now.AddDays(-1));
        if (pick == default) pick = all[0];
        try
        {
            using var fs = File.OpenRead(pick.Path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize(gz, SettingsJson.Default.Snapshot);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record GrowthItem(string RelativePath, DiskNode? Node, long Before, long Now)
{
    public long Delta => Now - Before;
}

public sealed record GrowthReport(DateTime Baseline, long UsedBefore, long UsedNow, IReadOnlyList<GrowthItem> Items)
{
    public long UsedDelta => UsedNow - UsedBefore;
}

/// <summary>Finds the folders that explain most of the change between two scans.</summary>
public static class GrowthAnalyzer
{
    public static GrowthReport Compare(ScanResult scan, Snapshot baseline, int maxItems = 15)
    {
        var current = Snapshot.From(scan);
        long threshold = Math.Max(256L << 20, (scan.TotalBytes / 2000));
        var deltas = new Dictionary<string, (long Before, long Now)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, size) in current.Folders)
        {
            baseline.Folders.TryGetValue(path, out var before);
            deltas[path] = (before, size);
        }
        foreach (var (path, before) in baseline.Folders)
        {
            if (!deltas.ContainsKey(path)) deltas[path] = (before, 0);
        }

        var big = deltas.Where(kv => Math.Abs(kv.Value.Now - kv.Value.Before) >= threshold)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        static string? ParentOf(string p)
        {
            int i = p.LastIndexOf('\\');
            return i <= 0 ? null : p[..i];
        }

        // A folder is a culprit unless one child explains at least 75% of its change.
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in big.Keys)
        {
            var parent = ParentOf(p);
            if (parent == null) continue;
            if (!childrenOf.TryGetValue(parent, out var list)) childrenOf[parent] = list = new();
            list.Add(p);
        }
        var culprits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, v) in big)
        {
            long delta = v.Now - v.Before;
            bool explainedByChild = childrenOf.TryGetValue(path, out var kids) &&
                kids.Any(k => Math.Sign(big[k].Now - big[k].Before) == Math.Sign(delta) &&
                              Math.Abs(big[k].Now - big[k].Before) >= Math.Abs(delta) * 0.75);
            if (!explainedByChild) culprits.Add(path);
        }

        // Drop a culprit when deeper culprits already explain most of its change.
        foreach (var path in culprits.OrderBy(p => p.Length).ToList())
        {
            long delta = big[path].Now - big[path].Before;
            long explained = culprits.Where(c => c.Length > path.Length && c.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) &&
                                                 !culprits.Any(o => o != c && o.Length > path.Length && c.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase) && o.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
                                     .Sum(c => big[c].Now - big[c].Before);
            if (delta != 0 && (double)explained / delta >= 0.6) culprits.Remove(path);
        }

        var items = culprits
            .Select(p => new GrowthItem(p, scan.Root.Find(p), big[p].Before, big[p].Now))
            .OrderByDescending(i => Math.Abs(i.Delta))
            .Take(maxItems)
            .ToList();
        return new GrowthReport(baseline.TakenAt, baseline.UsedBytes, scan.UsedBytes, items);
    }
}

/// <summary>Free-space samples per drive, written by the tray helper and the app.</summary>
public sealed class FreeSpaceLog
{
    public Dictionary<string, List<FreeSpaceSample>> Drives { get; set; } = new();

    public static FreeSpaceLog Load()
    {
        try
        {
            if (File.Exists(AppPaths.FreeSpaceFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.FreeSpaceFile), SettingsJson.Default.FreeSpaceLog) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new FreeSpaceLog();
    }

    /// <summary>Adds a sample unless one was taken in the last hour. Returns true when something changed.</summary>
    public bool Record(string drive, long free, DateTime now, int keepDays = 90)
    {
        var key = AppPaths.DriveKey(drive);
        if (!Drives.TryGetValue(key, out var list)) Drives[key] = list = new();
        if (list.Count > 0 && now - list[^1].At < TimeSpan.FromHours(1)) return false;
        list.Add(new FreeSpaceSample { At = now, Free = free });
        list.RemoveAll(s => s.At < now.AddDays(-keepDays));
        return true;
    }

    public long? FreeAround(string drive, DateTime when)
    {
        if (!Drives.TryGetValue(AppPaths.DriveKey(drive), out var list) || list.Count == 0) return null;
        var best = list.MinBy(s => Math.Abs((s.At - when).Ticks));
        return best != null && Math.Abs((best.At - when).TotalHours) < 6 ? best.Free : null;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(AppPaths.FreeSpaceFile, JsonSerializer.Serialize(this, SettingsJson.Default.FreeSpaceLog));
    }
}

public sealed class FreeSpaceSample
{
    public DateTime At { get; set; }
    public long Free { get; set; }
}
