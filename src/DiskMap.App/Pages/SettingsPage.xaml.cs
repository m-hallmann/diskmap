using DiskMap.Core;
using DiskMap.Core.Actions;
using DiskMap.Core.Layout;
using DiskMap.Core.Settings;
using DiskMap.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.Pages;

public sealed partial class SettingsPage : Page
{
    private static AppState S => AppState.Current;
    private static AppSettings Settings => S.Settings;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _loading = true;
        BuildDriveCards();
        IntervalBox.SelectedItem = IntervalBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Settings.CheckIntervalMinutes.ToString())
                                   ?? IntervalBox.Items[1];
        RemindBox.SelectedIndex = (int)Settings.Remind;
        StartupSwitch.IsOn = Settings.StartWithWindows;
        StartupNote.Text = S.IsElevated
            ? "Runs the small tray helper at sign-in with admin rights, so diskmap opens from the tray without a UAC prompt."
            : "Runs the small tray helper at sign-in. Restart diskmap as administrator to let it open with admin rights.";
        DeleteMode.SelectedIndex = Settings.DeletePermanently ? 0 : 1;
        LockedBox.SelectedIndex = (int)Settings.LockedFiles;
        HistoryBox.SelectedItem = HistoryBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Settings.HistoryDays.ToString())
                                  ?? HistoryBox.Items[1];
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Settings.Theme) ?? ThemeBox.Items[0];
        MapColorsBox.SelectedIndex = Settings.MapColors == MapColoring.Folder ? 0 : 1;
        MapTilingBox.SelectedIndex = Settings.MapTiling == MapTiling.Rows ? 0 : 1;

        ScanMethodTitle.Text = S.IsElevated ? "Fast scan: reading the NTFS file table" : "Folder walk (no admin rights)";
        ScanMethodNote.Text = S.IsElevated
            ? "Every file is counted once, by the space it really uses on disk, so hardlinks (System32 and WinSxS) and cloud-only files aren't double counted."
            : "Slower, and it can't see hardlinks or some system folders. Start diskmap as administrator for the fast, exact scan.";
        VersionText.Text = Updater.Describe();
        AboutLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(MainWindow.LogoUri(ActualTheme == ElementTheme.Dark)));
        DataFolderText.Text = Program.IsDemo ? "Demo mode: nothing is saved" : AppPaths.DataFolder;
        AutoUpdateSwitch.IsOn = Settings.AutoUpdate;
        AutoUpdateSwitch.IsEnabled = CheckUpdatesButton.IsEnabled = Updater.IsInstalled;
        UpdateNote.Text = Updater.IsInstalled
            ? "Downloads new releases from GitHub in the background and installs them when you restart diskmap."
            : "Available in the installed and portable versions from GitHub Releases.";
        _loading = false;
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.AutoUpdate = AutoUpdateSwitch.IsOn;
        Save();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateNote.Text = "Checking GitHub for a newer version…";
        var version = App.Current?.Window is { } w ? await w.CheckForUpdateAsync(TimeSpan.Zero) : null;
        UpdateNote.Text = version != null ? $"diskmap {version} is ready. Restart to update." : "You have the latest version.";
        CheckUpdatesButton.IsEnabled = true;
    }

    private void BuildDriveCards()
    {
        DriveCards.Children.Clear();
        foreach (var drive in S.Drives)
        {
            var alert = Settings.AlertFor(drive.Root, drive.Total);
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new FontIcon { Glyph = "", VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
            text.Children.Add(new TextBlock { Text = drive.Title });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            bar.Children.Add(new ProgressBar { Width = 120, Maximum = 100, Value = drive.UsedPercent, ShowPaused = drive.BelowLimit, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(new TextBlock { Text = drive.Subtitle, Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"] });
            text.Children.Add(bar);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var label = new TextBlock { Text = "Alert below", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);

            var number = new NumberBox
            {
                Value = alert.Threshold,
                Minimum = 1,
                Maximum = alert.Unit == ThresholdUnit.Percent ? 90 : 100_000,
                SmallChange = alert.Unit == ThresholdUnit.Percent ? 1 : 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 110,
                VerticalAlignment = VerticalAlignment.Center,
            };
            number.ValueChanged += (s, e) =>
            {
                if (_loading || double.IsNaN(e.NewValue)) return;
                alert.Threshold = e.NewValue;
                Save();
            };
            Grid.SetColumn(number, 3);
            grid.Children.Add(number);

            var unit = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
            unit.Items.Add("GB");
            unit.Items.Add("% of drive");
            unit.SelectedIndex = alert.Unit == ThresholdUnit.Percent ? 1 : 0;
            unit.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                alert.Unit = unit.SelectedIndex == 1 ? ThresholdUnit.Percent : ThresholdUnit.Gigabytes;
                number.Maximum = alert.Unit == ThresholdUnit.Percent ? 90 : 100_000;
                if (alert.Unit == ThresholdUnit.Percent && alert.Threshold > 90) number.Value = 10;
                Save();
            };
            Grid.SetColumn(unit, 4);
            grid.Children.Add(unit);

            var toggle = new ToggleSwitch { IsOn = alert.Enabled, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, OnContent = "On", OffContent = "Off" };
            toggle.Toggled += (_, _) =>
            {
                if (_loading) return;
                alert.Enabled = toggle.IsOn;
                Save();
            };
            Grid.SetColumn(toggle, 5);
            grid.Children.Add(toggle);

            DriveCards.Children.Add(new Border { Style = (Style)Application.Current.Resources["SettingsCardStyle"], Child = grid });
        }
    }

    private static void Save()
    {
        S.SaveSettings();
        S.RefreshDrives();
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IntervalBox.SelectedItem is not ComboBoxItem item) return;
        Settings.CheckIntervalMinutes = int.Parse((string)item.Tag);
        Save();
    }

    private void OnRemindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RemindBox.SelectedIndex < 0) return;
        Settings.Remind = (RemindPolicy)RemindBox.SelectedIndex;
        Save();
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.StartWithWindows = StartupSwitch.IsOn;
        Save();
        await TrayLink.SetAutostartAsync(StartupSwitch.IsOn);
        if (StartupSwitch.IsOn) TrayLink.EnsureRunning();
        else TrayLink.Stop();
    }

    private async void OnTestAlert(object sender, RoutedEventArgs e)
    {
        if (!TrayLink.IsRunning)
        {
            TrayLink.EnsureRunning();
            await Task.Delay(1500);
        }
        if (TrayLink.IsRunning) TrayLink.Post(TrayLink.MsgTestAlert);
        else S.ShowNotice(new Notice("Tray helper not found", "Build or install DiskMap.Tray.exe next to diskmap to get free-space alerts.",
            InfoBarSeverity.Warning));
    }

    private void OnDeleteModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeleteMode.SelectedIndex < 0) return;
        Settings.DeletePermanently = DeleteMode.SelectedIndex == 0;
        Save();
    }

    private void OnLockedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LockedBox.SelectedIndex < 0) return;
        Settings.LockedFiles = (LockedFilePolicy)LockedBox.SelectedIndex;
        Save();
    }

    private void OnHistoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HistoryBox.SelectedItem is not ComboBoxItem item) return;
        Settings.HistoryDays = int.Parse((string)item.Tag);
        Save();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        Settings.Theme = (string)item.Tag;
        Save();
        App.Current?.Window?.ApplyTheme();
    }

    private void OnMapColorsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MapColorsBox.SelectedIndex < 0) return;
        Settings.MapColors = MapColorsBox.SelectedIndex == 0 ? MapColoring.Folder : MapColoring.FileType;
        Save();
    }

    private void OnMapTilingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MapTilingBox.SelectedIndex < 0) return;
        Settings.MapTiling = MapTilingBox.SelectedIndex == 0 ? MapTiling.Rows : MapTiling.Squarified;
        Save();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        ShellLauncher.Open(AppPaths.DataFolder);
    }
}
