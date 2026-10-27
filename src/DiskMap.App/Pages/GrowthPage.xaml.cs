using System.ComponentModel;
using DiskMap.Core;
using DiskMap.Core.History;
using DiskMap.Core.Model;
using DiskMap.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DiskMap.Pages;

public sealed class GrowthRow : INotifyPropertyChanged
{
    private readonly GrowthItem _item;

    public GrowthRow(GrowthItem item, double maxDelta)
    {
        _item = item;
        var node = item.Node;
        Title = node?.DisplayName ?? Path.GetFileName(item.RelativePath);
        string what = item.Before == 0 ? "New folder" : item.Now == 0 ? "Gone" : item.Delta > 0 ? "Grew" : "Shrank";
        Detail = $"{what} · {AppState.Current.Drive?.Letter}{item.RelativePath}";
        DeltaText = Format.Delta(item.Delta);
        UpVisibility = item.Delta >= 0 ? Visibility.Visible : Visibility.Collapsed;
        DownVisibility = item.Delta >= 0 ? Visibility.Collapsed : Visibility.Visible;
        BarWidth = Math.Max(3, 120 * Math.Abs(item.Delta) / Math.Max(1, maxDelta));
        Swatch = Dialogs.CategoryBrush(node?.Category ?? Category.Other);
    }

    public string Title { get; }
    public string Detail { get; }
    public string DeltaText { get; }
    public Visibility UpVisibility { get; }
    public Visibility DownVisibility { get; }
    public double BarWidth { get; }
    public Brush Swatch { get; }

    private bool Alive => _item.Node is { } n && n.Root == AppState.Current.Scan?.Root;
    public bool CanSelect => Alive && _item.Node!.CanDelete;
    public bool CanShow => Alive;
    public double RowOpacity => Alive || _item.Node == null ? 1 : 0.45;
    public string SizeText => _item.Node == null ? Format.Bytes(_item.Now) : Alive ? Format.Bytes(_item.Node.Size) : "Deleted";

    public bool IsChecked
    {
        get => _item.Node != null && AppState.Current.Selection.Contains(_item.Node);
        set
        {
            if (_item.Node != null && value != IsChecked) AppState.Current.Toggle(_item.Node, value);
        }
    }

    public void ShowInMap()
    {
        if (!Alive) return;
        AppState.Current.Reveal(_item.Node!);
        AppState.Current.Click(_item.Node!, add: false);
        App.Current?.Window?.NavigateTo("map");
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class GrowthPage : Page
{
    private static AppState S => AppState.Current;
    private List<GrowthRow> _rows = new();
    private List<FreeSpaceSample> _samples = new();

    public GrowthPage()
    {
        InitializeComponent();
        Motion.EnableShowHide(DeleteButton, 0);
        Motion.EnableShowHide(ClearButton, 0);
        Loaded += (_, _) =>
        {
            S.Changed += OnStateChanged;
            Build();
        };
        Unloaded += (_, _) => S.Changed -= OnStateChanged;
    }

    private void OnStateChanged(StateChange change)
    {
        switch (change)
        {
            case StateChange.Scanned:
                Build();
                break;
            case StateChange.Selection:
            case StateChange.Tree:
            case StateChange.Drives:
                foreach (var r in _rows) r.Refresh();
                UpdateHeader();
                break;
        }
    }

    private void Build()
    {
        var drive = S.Drive;
        var report = drive?.Growth;
        _rows = report == null ? new() : report.Items.Select(i => new GrowthRow(i, report.Items.Max(x => Math.Abs(x.Delta)))).ToList();
        Rows.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (drive == null || drive.Scan == null)
        {
            Subtitle.Text = S.IsScanning ? S.ScanStatus : "";
            EmptyText.Text = S.IsScanning ? "Waiting for the scan to finish." : "Pick a drive to scan.";
        }
        else if (report == null)
        {
            Subtitle.Text = $"First scan of {drive.Letter}";
            EmptyText.Text = $"This is the first scan of {drive.Letter} diskmap has seen. It keeps folder totals after every scan, so next time you'll see exactly which folders grew.";
        }
        else
        {
            var ago = DateTime.Now - report.Baseline;
            Subtitle.Text = $"Compared with the scan from {report.Baseline:MMM d, yyyy} ({Format.Ago(ago)})";
            EmptyText.Text = "Nothing changed by more than a few hundred MB.";
        }

        _samples = drive == null ? new() : S.FreeSpaceSamples(drive.Root).Where(s => s.At > DateTime.Now.AddDays(-30)).ToList();
        UpdateHeader();
        DrawSpark();
    }

    private void UpdateHeader()
    {
        var drive = S.Drive;
        var report = drive?.Growth;
        if (drive?.Scan is { } scan)
        {
            long usedNow = drive.Total - drive.Free;
            long delta = report != null ? usedNow - report.UsedBefore : 0;
            UsedCaption.Text = report != null ? $"Used since {report.Baseline:MMM d}" : "Used";
            UsedText.Text = report != null ? Format.Delta(delta) : Format.Bytes(usedNow);
            UsedText.Style = Dialogs.AppStyle(report != null && delta > 0 ? "CautionBigNumberStyle" : "BigNumberStyle");
            UsedNote.Text = report != null ? $"{report.Items.Count} folders changed" : $"of {Format.Bytes(scan.TotalBytes)}";
            FreeCaption.Text = $"Free on {drive.Letter}";
            FreeText.Text = Format.Bytes(drive.Free);
            LimitText.Text = $"You get an alert below {S.Settings.AlertFor(drive.Root, drive.Total).Describe()}";
        }
        var top = TreeOps.TopLevel(S.Selection);
        bool any = top.Count > 0;
        DeleteButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any) DeleteText.Text = $"Delete {(top.Count == 1 ? "1 item" : $"{top.Count:N0} items")} · {Format.Bytes(top.Sum(n => n.Size))}";
    }

    private void OnSparkSizeChanged(object sender, SizeChangedEventArgs e) => DrawSpark();

    private void DrawSpark()
    {
        var drive = S.Drive;
        double w = Spark.ActualWidth, h = Spark.Height;
        bool enough = _samples.Count >= 2 && drive != null && w > 0;
        SparkEmpty.Visibility = enough ? Visibility.Collapsed : Visibility.Visible;
        Spark.Visibility = enough ? Visibility.Visible : Visibility.Collapsed;
        if (!enough) return;

        long limit = S.Settings.AlertFor(drive!.Root, drive.Total).ThresholdBytes(drive.Total);
        double max = Math.Max(_samples.Max(s => s.Free), limit) * 1.1;
        var start = _samples[0].At;
        double span = Math.Max(1, (_samples[^1].At - start).TotalSeconds);
        double Y(long v) => 3 + (1 - v / max) * (h - 6);
        var points = new PointCollection();
        foreach (var s in _samples) points.Add(new Point((s.At - start).TotalSeconds / span * w, Y(s.Free)));
        SparkLine.Points = points;
        SparkLimit.X1 = 0;
        SparkLimit.X2 = w;
        SparkLimit.Y1 = SparkLimit.Y2 = Y(limit);
    }

    private void OnClear(object sender, RoutedEventArgs e) => S.ClearSelection();

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var nodes = S.Selection.ToList();
        if (nodes.Count == 0 || S.Drive == null) return;
        if (!await Dialogs.ConfirmDeleteAsync(XamlRoot, nodes, S.Settings.DeletePermanently, S.Drive.Letter)) return;
        int count = TreeOps.TopLevel(nodes).Count;
        var report = await S.DeleteAsync(nodes, count == 1 ? "Deleted 1 item" : $"Deleted {count:N0} items");
        await Dialogs.HandleFailuresAsync(XamlRoot, report);
    }
}
