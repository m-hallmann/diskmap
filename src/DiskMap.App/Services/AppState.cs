using System.Collections.ObjectModel;
using System.ComponentModel;
using DiskMap.Core;
using DiskMap.Core.Actions;
using DiskMap.Core.Demo;
using DiskMap.Core.History;
using DiskMap.Core.Model;
using DiskMap.Core.Native;
using DiskMap.Core.Query;
using DiskMap.Core.Scanning;
using DiskMap.Core.Settings;
using Microsoft.UI.Dispatching;

namespace DiskMap.Services;

public enum StateChange { Drives, ScanStarted, ScanProgress, Scanned, Folder, Selection, Filter, Tree, Notice }

public sealed record Notice(string Title, string Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity, bool Busy = false);

public sealed class DriveEntry : INotifyPropertyChanged
{
    public DriveEntry(string root, string label)
    {
        Root = root;
        Label = label;
    }

    public string Root { get; }
    public string Label { get; }
    public string Letter => Root.TrimEnd('\\');
    public string Title => $"{Label} ({Letter})";
    public long Total { get; private set; }
    public long Free { get; private set; }
    public long Limit { get; private set; }
    public bool BelowLimit => Free < Limit;
    public double UsedPercent => Total > 0 ? 100.0 * (Total - Free) / Total : 0;
    public string Subtitle => $"{Format.Bytes(Free)} free of {Format.Bytes(Total)}";

    public ScanResult? Scan { get; set; }
    public GrowthReport? Growth { get; set; }
    public bool HasBaseline { get; set; }
    public List<Suggestion> Suggestions { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Demo drives report made-up sizes instead of asking Windows.</summary>
    public bool IsDemo { get; private set; }

    public void SetFixed(long total, long free, AppSettings settings)
    {
        IsDemo = true;
        Total = total;
        Free = free;
        Refresh(settings);
    }

    public void Refresh(AppSettings settings)
    {
        if (!IsDemo && Volume.TryGetFreeSpace(Root, out var free, out var total))
        {
            Free = free;
            Total = total;
        }
        Limit = settings.AlertFor(Root, Total).ThresholdBytes(Total);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

/// <summary>Everything the pages share: drives, the current scan, selection, search and notices.</summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _filterCts;
    private DispatcherQueue? _dispatcher;

    public AppSettings Settings { get; private set; } = AppSettings.Load();
    public ObservableCollection<DriveEntry> Drives { get; } = new();
    public DriveEntry? Drive { get; private set; }
    public ScanResult? Scan => Drive?.Scan;
    /// <summary>Demo mode shows the admin experience; its actions are simulated anyway.</summary>
    public bool IsElevated => Program.IsDemo || Elevation.IsElevated;

    public bool IsScanning { get; private set; }
    public string ScanStatus { get; private set; } = "";
    public double ScanFraction { get; private set; } = -1;

    public DiskNode? Folder { get; private set; }
    public DiskNode? Focus { get; private set; }
    public HashSet<DiskNode> Selection { get; } = new(ReferenceEqualityComparer.Instance);
    public DiskNode? Hover { get; set; }

    public string Query { get; private set; } = "";
    public Category? CategoryFilter { get; private set; }
    public HashSet<DiskNode>? FilterHits { get; private set; }
    public List<DiskNode> FilterMatches { get; private set; } = new();
    public long FilterBytes { get; private set; }
    public bool ShowGrowth { get; set; } = true;

    public Notice? CurrentNotice { get; private set; }

    public event Action<StateChange>? Changed;

    private void Raise(StateChange change) => Changed?.Invoke(change);

#if DEBUG
    internal void DebugRaise(StateChange change) => Raise(change);
#endif

    private List<FreeSpaceSample>? _demoFreeSpace;

    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (Program.IsDemo)
        {
            Settings = new AppSettings();
            var c = new DriveEntry("C:\\", "Local Disk");
            c.SetFixed(DemoData.TotalBytes, DemoData.FreeBytes, Settings);
            var d = new DriveEntry("D:\\", "Data");
            d.SetFixed(476L << 30, 212L << 30, Settings);
            Drives.Add(c);
            Drives.Add(d);
            Raise(StateChange.Drives);
            return;
        }

        // A portable copy shouldn't add itself to startup unless asked to.
        if (!File.Exists(AppPaths.SettingsFile) && Updater.IsPortable) Settings.StartWithWindows = false;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
            var entry = new DriveEntry(d.RootDirectory.FullName, label);
            entry.Refresh(Settings);
            Drives.Add(entry);
        }
        SaveSettings();
        Raise(StateChange.Drives);
    }

    /// <summary>Free-space history for the chart on What grew.</summary>
    public List<FreeSpaceSample> FreeSpaceSamples(string driveRoot)
    {
        if (Program.IsDemo) return _demoFreeSpace ?? new();
        return FreeSpaceLog.Load().Drives.TryGetValue(AppPaths.DriveKey(driveRoot), out var list) ? list : new();
    }

    public void SaveSettings()
    {
        if (Program.IsDemo) return;
        try { Settings.Save(); }
        catch (IOException ex) { Log.Write("Saving settings failed: " + ex.Message); }
        TrayLink.Post(TrayLink.MsgReloadSettings);
    }

    public void RefreshDrives()
    {
        foreach (var d in Drives) d.Refresh(Settings);
        Raise(StateChange.Drives);
    }

    // ---- Scanning ---------------------------------------------------------------------------

    public async Task ScanAsync(DriveEntry drive)
    {
        if (Program.IsDemo)
        {
            LoadDemo(drive);
            return;
        }
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        Drive = drive;
        IsScanning = true;
        ScanStatus = $"Scanning {drive.Letter}…";
        ScanFraction = -1;
        Folder = null;
        Focus = null;
        Selection.Clear();
        FilterHits = null;
        Raise(StateChange.ScanStarted);

        var progress = new Progress<ScanProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            ScanStatus = p.Fraction >= 0
                ? $"Reading the file table of {drive.Letter}… {Format.Count(p.Items)} records"
                : $"Scanning {drive.Letter}… {Format.Count(p.Items)} items, {Format.Bytes(p.Bytes)}";
            ScanFraction = p.Fraction;
            Raise(StateChange.ScanProgress);
        });

        try
        {
            var (scan, snapshot) = await Task.Run(() =>
            {
                ScanResult result;
                if (MftScanner.CanScan(drive.Root))
                {
                    try
                    {
                        result = new MftScanner().Scan(drive.Root, progress, cts.Token);
                        foreach (var w in result.Warnings) Log.Write($"MFT scan of {drive.Root}: {w}");
                        return (result, Snapshot.From(result));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Write($"MFT scan of {drive.Root} failed, walking folders instead: {ex.Message}");
                    }
                }
                result = new FolderScanner().Scan(drive.Root, progress, cts.Token);
                return (result, Snapshot.From(result));
            }, cts.Token);

            // Scanning leaves a lot of short-lived garbage behind. Collect it in the background
            // (a blocking, compacting collection froze the window for a second on big drives).
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);

            var after = System.Diagnostics.Stopwatch.StartNew();
            var baseline = await Task.Run(() => SnapshotStore.LoadBaseline(drive.Root, DateTime.Now));
            drive.Scan = scan;
            drive.HasBaseline = baseline != null;
            drive.Growth = baseline != null ? GrowthAnalyzer.Compare(scan, baseline) : null;
            drive.Suggestions = CleanupService.Analyze(scan);
            _ = Task.Run(() =>
            {
                try
                {
                    SnapshotStore.Save(drive.Root, snapshot, Settings.HistoryDays);
                    var log = FreeSpaceLog.Load();
                    if (log.Record(drive.Root, scan.FreeBytes, DateTime.Now, Settings.HistoryDays)) log.Save();
                }
                catch (IOException ex)
                {
                    Log.Write("Saving history failed: " + ex.Message);
                }
            });

            if (cts.IsCancellationRequested) return;
            drive.Refresh(Settings);
            Folder = scan.Root;
            IsScanning = false;
            Log.Write($"Scanned {drive.Root} with {scan.Method} in {scan.Duration.TotalSeconds:0.0}s, {scan.ItemCount:N0} items " +
                      $"({scan.Timings}; history and cleanup {after.ElapsedMilliseconds} ms).");
            Raise(StateChange.Scanned);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("Scan failed: " + ex);
            IsScanning = false;
            ShowNotice(new Notice("The scan failed", ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error));
            Raise(StateChange.Scanned);
        }
    }

    private void LoadDemo(DriveEntry drive)
    {
        if (drive.Root != "C:\\")
        {
            ShowNotice(new Notice("Demo mode", "Only the sample drive C: has data.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational));
            return;
        }
        Drive = drive;
        var scan = DemoData.Create(out var baseline, out _demoFreeSpace);
        drive.Scan = scan;
        drive.HasBaseline = true;
        drive.Growth = GrowthAnalyzer.Compare(scan, baseline);
        drive.Suggestions = DemoData.Suggestions(scan);
        drive.SetFixed(scan.TotalBytes, scan.FreeBytes, Settings);
        Folder = scan.Root;
        Focus = null;
        Selection.Clear();
        FilterHits = null;
        IsScanning = false;
        Raise(StateChange.Scanned);
        if (scan.Root.Find(@"Users\alex\AI\llm-models") is { } models) Click(models, add: false);
    }

    // ---- Navigation and selection -----------------------------------------------------------

    public void OpenFolder(DiskNode folder)
    {
        if (folder.Children == null) return;
        Folder = folder;
        Focus = null;
        Raise(StateChange.Folder);
    }

    public void Up()
    {
        if (Folder?.Parent is { } p) OpenFolder(p);
    }

    public void SetFocus(DiskNode? node)
    {
        Focus = node;
        Raise(StateChange.Selection);
    }

    /// <summary>Plain click: select just this item. Ctrl or Shift click: add or remove it.</summary>
    public void Click(DiskNode node, bool add)
    {
        if (add)
        {
            if (!Selection.Remove(node) && node.CanDelete) Selection.Add(node);
        }
        else
        {
            Selection.Clear();
            if (node.CanDelete) Selection.Add(node);
        }
        Focus = node;
        Raise(StateChange.Selection);
    }

    public void Toggle(DiskNode node, bool selected)
    {
        if (selected && node.CanDelete) Selection.Add(node);
        else Selection.Remove(node);
        Raise(StateChange.Selection);
    }

    public void SelectMany(IEnumerable<DiskNode> nodes)
    {
        Selection.Clear();
        foreach (var n in nodes.Where(n => n.CanDelete)) Selection.Add(n);
        Raise(StateChange.Selection);
    }

    public void ClearSelection()
    {
        Selection.Clear();
        Raise(StateChange.Selection);
    }

    /// <summary>Opens the folder that contains <paramref name="node"/> and focuses it.</summary>
    public void Reveal(DiskNode node)
    {
        Folder = node.Parent ?? node;
        Focus = node;
        Raise(StateChange.Folder);
    }

    public long SelectionBytes => TreeOps.TopLevel(Selection).Sum(n => n.Size);

    // ---- Search -----------------------------------------------------------------------------

    public void SetFilter(string query, Category? category)
    {
        Query = query;
        CategoryFilter = category;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        var root = Scan?.Root;
        if (root == null || (string.IsNullOrWhiteSpace(query) && category == null))
        {
            FilterHits = null;
            FilterMatches = new();
            FilterBytes = 0;
            Raise(StateChange.Filter);
            return;
        }

        var q = NodeQuery.Parse(query, category);
        _ = Task.Run(() =>
        {
            var hits = q.Evaluate(root, out var matches, out var bytes);
            _dispatcher?.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested) return;
                FilterHits = hits;
                FilterMatches = matches;
                FilterBytes = bytes;
                Raise(StateChange.Filter);
            });
        });
    }

    // ---- Deleting ---------------------------------------------------------------------------

    public void ShowNotice(Notice? notice)
    {
        CurrentNotice = notice;
        Raise(StateChange.Notice);
    }

    public async Task<DeleteReport?> DeleteAsync(IReadOnlyCollection<DiskNode> nodes, string doneText)
    {
        var scan = Scan;
        if (scan == null) return null;
        var targets = TreeOps.TopLevel(nodes).Where(n => n.CanDelete).ToList();
        if (targets.Count == 0) return null;

        long usedBefore = scan.Root.Size;
        ShowNotice(new Notice("Deleting…", $"{targets.Count:N0} items, {Format.Bytes(targets.Sum(t => t.Size))}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, Busy: true));
        var paths = targets.ToDictionary(t => t.FullPath, t => t, StringComparer.OrdinalIgnoreCase);
        DeleteReport report;
        if (Program.IsDemo)
        {
            // Demo paths can exist on a real PC (e.g. Steam folders), so never touch the disk here.
            await Task.Delay(350);
            report = new DeleteReport();
            report.Removed.AddRange(paths.Keys);
        }
        else
        {
            report = await DeleteService.DeleteAsync(paths.Keys.ToList(), Settings.DeletePermanently, null, CancellationToken.None);
        }

        long cluster = Volume.ClusterSize(scan.DriveRoot);
        foreach (var path in report.Removed) TreeOps.Remove(paths[path]);
        foreach (var path in report.Partial)
        {
            var node = paths[path];
            if (node.IsDirectory) TreeOps.Replace(node, FolderScanner.Rescan(path, node.Name, cluster));
        }
        AfterTreeChange();

        long freed = usedBefore - scan.Root.Size;
        string where = Settings.DeletePermanently ? "" : " (moved to the Recycle Bin)";
        if (report.Succeeded)
        {
            ShowNotice(new Notice(doneText + where, $"Freed {Format.Bytes(freed)}. {Drive!.Letter} now has {Format.Bytes(Drive.Free)} free.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success));
        }
        else
        {
            ShowNotice(new Notice($"Some items couldn't be deleted", $"Freed {Format.Bytes(freed)}. {report.Failures.Count:N0} items failed.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
        }
        return report;
    }

    public async Task RunSuggestionAsync(Suggestion s)
    {
        var scan = Scan;
        if (scan == null) return;
        Drive!.Refresh(Settings);
        long freeBefore = Drive.Free;
        ShowNotice(new Notice(s.Title + "…", "Working on it.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, Busy: true));
        if (Program.IsDemo)
        {
            await Task.Delay(500);
            foreach (var node in s.Nodes.ToList())
            {
                if (node.IsDirectory) foreach (var child in node.Children!.ToList()) TreeOps.Remove(child);
                else TreeOps.Remove(node);
            }
            long before = Drive.Free;
            AfterTreeChange();
            ShowNotice(new Notice(s.DoneText, $"Freed {Format.Bytes(Drive.Free - before)}. {Drive.Letter} now has {Format.Bytes(Drive.Free)} free.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success));
            return;
        }
        string? error = null;
        try
        {
            if (s.Kind == SuggestionKind.EmptyFolders)
            {
                var report = await CleanupService.EmptyFoldersAsync(s.Nodes, CancellationToken.None);
                if (!report.Succeeded) error = $"{report.Failures.Count:N0} items were in use or protected and were skipped.";
            }
            else if (s.Run != null)
            {
                error = await s.Run(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
        }

        long cluster = Volume.ClusterSize(scan.DriveRoot);
        foreach (var node in s.Nodes)
        {
            if (node.Parent == null) continue;
            var path = node.FullPath;
            if (node.IsDirectory && Directory.Exists(path)) TreeOps.Replace(node, FolderScanner.Rescan(path, node.Name, cluster));
            else if (!node.IsDirectory && !File.Exists(path)) TreeOps.Remove(node);
        }
        AfterTreeChange();
        long freed = Math.Max(0, Drive.Free - freeBefore);
        if (error == null)
        {
            ShowNotice(new Notice(s.DoneText, $"Freed {Format.Bytes(freed)}. {Drive!.Letter} now has {Format.Bytes(Drive.Free)} free.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success));
        }
        else
        {
            ShowNotice(new Notice(s.Title, error, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
        }
    }

    private void AfterTreeChange()
    {
        var scan = Scan!;
        Selection.RemoveWhere(n => n.Root != scan.Root);
        if (Focus != null && Focus.Root != scan.Root) Focus = null;
        if (Folder != null && Folder.Root != scan.Root)
        {
            var f = Folder;
            while (f.Parent != null && f.Root != scan.Root) f = f.Parent;
            Folder = f.Root == scan.Root ? f : scan.Root;
        }
        if (Program.IsDemo)
        {
            scan.FreeBytes = scan.TotalBytes - scan.Root.Size;
            Drive!.SetFixed(scan.TotalBytes, scan.FreeBytes, Settings);
        }
        else
        {
            scan.RefreshFreeSpace();
            Drive!.Refresh(Settings);
        }
        Drive.Suggestions = CleanupService.Analyze(scan);
        if (FilterHits != null) SetFilter(Query, CategoryFilter);
        Raise(StateChange.Tree);
        Raise(StateChange.Drives);
    }
}
