using System.Diagnostics;
using DiskMap.Core;
using DiskMap.Core.Native;
using DiskMap.Pages;
using DiskMap.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace DiskMap;

public sealed partial class MainWindow : Window
{
    private static readonly string[] s_pageOrder = ["map", "growth", "cleanup", "settings"];
    private string _currentPage = "";
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly HashSet<string> _lowSpaceDismissed = new(StringComparer.OrdinalIgnoreCase);

    public AppState State => AppState.Current;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "diskmap.ico"));
        SizeAndCenter();

        foreach (var bar in new UIElement[] { UpdateBar, ElevationBar, LowSpaceBar, NoticeBar }) Motion.EnableShowHide(bar);
        UpdateBar.CloseButtonClick += (_, _) => UpdateBar.Visibility = Visibility.Collapsed;
        Motion.EnableReposition(ContentFrame);
        LowSpaceBar.CloseButtonClick += (_, _) =>
        {
            if (State.Drive != null) _lowSpaceDismissed.Add(State.Drive.Root);
            LowSpaceBar.Visibility = Visibility.Collapsed;
        };
        ElevationBar.CloseButtonClick += (_, _) => ElevationBar.Visibility = Visibility.Collapsed;
        NoticeBar.CloseButtonClick += (_, _) => State.ShowNotice(null);

        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            State.SetFilter(SearchBox.Text, State.CategoryFilter);
        };

        State.Initialize(DispatcherQueue);
        State.Changed += OnStateChanged;
        _themeOverride = ArgValue(args, "--theme");
        ApplyTheme();
        ElevationBar.Visibility = State.IsElevated || Program.IsDemo ? Visibility.Collapsed : Visibility.Visible;

        bool sideEffects = !Program.IsSelfTest && !Program.IsDemo;
        if (sideEffects) TrayLink.RememberAppPath();
        if (State.Settings.StartWithWindows && sideEffects)
        {
            TrayLink.EnsureRunning();
            _ = TrayLink.SetAutostartAsync(true);
        }
        if (sideEffects && State.Settings.AutoUpdate) _ = CheckForUpdateAsync(TimeSpan.FromSeconds(8));
        if (Program.IsDemo) AppWindow.Resize(new SizeInt32(1600, 1000));

        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settings) settings.Content = "Settings";
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;
        }

        Nav.SelectedItem = MapItem;
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        DriveList.SelectedItem = State.Drives.FirstOrDefault(d => string.Equals(d.Root, systemRoot, StringComparison.OrdinalIgnoreCase))
                                 ?? State.Drives.FirstOrDefault();
        // The drive list selects its first drive once it loads; apply --growth etc. after that.
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _startupDone = true;
            HandleArgs(args);
        });
    }

    private bool _startupDone;

    private void SizeAndCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(area.Width, Math.Max(1100, (int)(area.Width * 0.72)));
        int h = Math.Min(area.Height, Math.Max(760, (int)(area.Height * 0.86)));
        w = Math.Min(w, (int)(h * 1.75));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    public void BringToFront(string[] args)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        HandleArgs(args);
    }

    private void HandleArgs(string[] args)
    {
        if (args.Contains("--settings")) NavigateTo("settings");
        else if (args.Contains("--growth")) NavigateTo("growth");
        else if (args.Contains("--cleanup")) NavigateTo("cleanup");
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private string? _themeOverride;

    /// <summary>The logo has a brighter variant so it doesn't look muddy on dark backgrounds.</summary>
    public static string LogoUri(bool dark) => dark ? "ms-appx:///Assets/logo-dark.png" : "ms-appx:///Assets/logo-256.png";

    /// <summary>Checks GitHub for a newer release and offers to restart into it.</summary>
    public async Task<string?> CheckForUpdateAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        var version = await Updater.CheckAndDownloadAsync();
        if (version != null)
        {
            UpdateBar.Message = $"diskmap {version} has been downloaded.";
            UpdateBar.IsOpen = true;
            UpdateBar.Visibility = Visibility.Visible;
        }
        return version;
    }

    private void OnApplyUpdate(object sender, RoutedEventArgs e) => Updater.ApplyAndRestart();

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = (_themeOverride ?? State.Settings.Theme) switch
            {
                "Light" or "light" => ElementTheme.Light,
                "Dark" or "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            bool dark = root.ActualTheme == ElementTheme.Dark;
            AppTitleBar.IconSource = new ImageIconSource
            {
                ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(LogoUri(dark))),
            };
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(18, 0, 0, 0);
            tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        }
    }

    // ---- Navigation -------------------------------------------------------------------------

    public void NavigateTo(string page)
    {
        Nav.SelectedItem = page switch
        {
            "growth" => GrowthItem,
            "cleanup" => CleanupItem,
            "settings" => Nav.SettingsItem,
            _ => MapItem,
        };
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "map";
        if (page == _currentPage) return;
        var type = page switch
        {
            "growth" => typeof(GrowthPage),
            "cleanup" => typeof(CleanupPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(MapPage),
        };
        int from = Array.IndexOf(s_pageOrder, _currentPage), to = Array.IndexOf(s_pageOrder, page);
        NavigationTransitionInfo transition = from < 0
            ? new EntranceNavigationTransitionInfo()
            : new SlideNavigationTransitionInfo { Effect = to > from ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft };
        _currentPage = page;
        ContentFrame.Navigate(type, null, transition);
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is not DriveEntry drive) return;
        if (!ReferenceEquals(State.Drive, drive) || drive.Scan == null)
        {
            _ = State.ScanAsync(drive);
        }
        if (_startupDone) NavigateTo("map");
    }

    // ---- Search -----------------------------------------------------------------------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
        if (!string.IsNullOrWhiteSpace(sender.Text)) NavigateTo("map");
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        State.SetFilter(sender.Text, State.CategoryFilter);
        NavigateTo("map");
    }

    public void ClearSearchBox() => SearchBox.Text = "";

    // ---- Banners ----------------------------------------------------------------------------

#if DEBUG
    /// <summary>Drives zoom, filter and morph transitions so their frame timings land in the log.</summary>
    private async Task SelfTestAsync()
    {
        await Task.Delay(2000);
        var root = State.Scan!.Root;
        var a = root.Children!.FirstOrDefault(c => c.IsDirectory && c.Children is { Count: > 1 });
        if (a == null) return;
        Log.Write("selftest: zoom into " + a.Name);
        State.OpenFolder(a);
        await Task.Delay(1500);
        var b = a.Children!.FirstOrDefault(c => c.IsDirectory && c.Children is { Count: > 1 });
        if (b != null)
        {
            Log.Write("selftest: zoom into " + b.Name);
            State.OpenFolder(b);
            await Task.Delay(1500);
            Log.Write("selftest: zoom out");
            State.Up();
            await Task.Delay(1500);
        }
        Log.Write("selftest: zoom out to root");
        State.Up();
        await Task.Delay(1500);
        Log.Write("selftest: filter");
        State.SetFilter("", Core.Model.Category.Caches);
        await Task.Delay(1500);
        State.SetFilter("", null);
        await Task.Delay(1500);
        Log.Write("selftest: morph");
        State.DebugRaise(StateChange.Tree);
        await Task.Delay(1500);
        Log.Write("selftest: done");
    }
#endif

    private void OnStateChanged(StateChange change)
    {
#if DEBUG
        if (change == StateChange.Scanned && State.Scan != null && Program.IsSelfTest)
        {
            _ = SelfTestAsync();
        }
#endif
        switch (change)
        {
            case StateChange.Drives:
            case StateChange.Scanned:
            case StateChange.Tree:
                UpdateLowSpaceBar();
                UpdateBadges();
                break;
            case StateChange.Notice:
                UpdateNotice();
                break;
            case StateChange.Filter when string.IsNullOrEmpty(State.Query) && SearchBox.Text.Length > 0 && State.FilterHits == null:
                SearchBox.Text = "";
                break;
        }
    }

    private void UpdateBadges()
    {
        var d = State.Drive;
        GrowthBadge.Text = d?.Growth is { } g ? Format.Delta(g.UsedDelta) : "";
        long cleanup = d?.Suggestions.Where(s => s.Kind != Core.Actions.SuggestionKind.Review).Sum(s => s.Bytes) ?? 0;
        CleanupBadge.Text = cleanup > 0 ? Format.Bytes(cleanup) : "";
    }

    private void UpdateLowSpaceBar()
    {
        var d = State.Drive;
        if (d == null || !d.BelowLimit || _lowSpaceDismissed.Contains(d.Root))
        {
            LowSpaceBar.Visibility = Visibility.Collapsed;
            return;
        }
        var alert = State.Settings.AlertFor(d.Root, d.Total);
        LowSpaceBar.Title = $"{d.Letter} is below your {alert.Describe()} alert";
        var top = d.Growth?.Items.FirstOrDefault(i => i.Delta > 0);
        LowSpaceBar.Message = top != null
            ? $"{Format.Bytes(d.Free)} free. Most growth since {d.Growth!.Baseline:MMM d}: {top.Node?.DisplayName ?? top.RelativePath.TrimStart('\\')} ({Format.Delta(top.Delta)})."
            : $"{Format.Bytes(d.Free)} free.";
        LowSpaceAction.Visibility = d.Growth is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        LowSpaceBar.IsOpen = true;
        LowSpaceBar.Visibility = Visibility.Visible;
    }

    private void UpdateNotice()
    {
        var n = State.CurrentNotice;
        if (n == null)
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            return;
        }
        NoticeBar.Title = n.Title;
        NoticeBar.Message = n.Message;
        NoticeBar.Severity = n.Severity;
        NoticeBar.IsClosable = !n.Busy;
        NoticeProgress.Visibility = n.Busy ? Visibility.Visible : Visibility.Collapsed;
        NoticeBar.IsOpen = true;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void OnSeeWhatGrew(object sender, RoutedEventArgs e) => NavigateTo("growth");

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        // Release the single-instance key first so the elevated copy doesn't hand itself back to us.
        var me = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
        me.UnregisterKey();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was cancelled; keep running as we are.
            Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(Program.InstanceKey);
        }
    }
}
