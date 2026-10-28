using DiskMap.Controls;
using DiskMap.Core;
using DiskMap.Core.Actions;
using DiskMap.Core.Games;
using DiskMap.Core.Layout;
using DiskMap.Core.Model;
using DiskMap.Core.Settings;
using DiskMap.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DiskMap.Pages;

public sealed partial class MapPage : Page
{
    private static AppState S => AppState.Current;

    private DiskNode? _shownFolder;
    private DiskNode? _shownRoot;
    private object? _detailsFor;
    private List<DiskNode> _crumbNodes = new();

    public MapPage()
    {
        InitializeComponent();
        Map.BoxClicked += OnBoxClicked;
        Map.BoxDoubleClicked += OnBoxDoubleClicked;
        Map.BoxRightClicked += OnBoxRightClicked;
        Map.HoverChanged += OnHoverChanged;
        Motion.EnableShowHide(FilterBar);
        Motion.EnableShowHide(DeleteButton, 0);
        Motion.EnableShowHide(ClearSelectionButton, 0);
        Motion.EnableShowHide(ScanOverlay, 6);

        Loaded += (_, _) =>
        {
            S.Changed += OnStateChanged;
            GrowthToggle.IsChecked = S.ShowGrowth;
            SyncAll(MapTransition.Fade);
        };
        Unloaded += (_, _) => S.Changed -= OnStateChanged;
    }

    private void OnStateChanged(StateChange change)
    {
        switch (change)
        {
            case StateChange.ScanStarted:
            case StateChange.ScanProgress:
                UpdateScanOverlay();
                break;
            case StateChange.Scanned:
                SyncAll(MapTransition.Unfold);
                break;
            case StateChange.Folder:
                ShowFolder();
                UpdateCrumbs();
                UpdateChips();
                UpdateDetails();
                UpdateSelectionUi();
                break;
            case StateChange.Selection:
                PushSelectionToMap();
                Map.InvalidateMap();
                UpdateDetails();
                UpdateSelectionUi();
                break;
            case StateChange.Filter:
                Map.FilterHits = S.FilterHits;
                Map.InvalidateMap(fade: true);
                UpdateFilterBar();
                UpdateChips();
                break;
            case StateChange.Tree:
                PushSelectionToMap();
                PushGrowthToMap();
                if (!ReferenceEquals(S.Folder, _shownFolder)) ShowFolder();
                else Map.Relayout(MapTransition.Morph);
                UpdateCrumbs();
                UpdateChips();
                UpdateDetails();
                UpdateSelectionUi();
                UpdateStatus(null);
                break;
            case StateChange.Drives:
                UpdateStatus(null);
                break;
        }
    }

    private void SyncAll(MapTransition transition)
    {
        UpdateScanOverlay();
        PushSelectionToMap();
        PushGrowthToMap();
        Map.FilterHits = S.FilterHits;
        Map.ColorByFolder = S.Settings.MapColors == MapColoring.Folder;
        Map.Tiling = S.Settings.MapTiling;
        if (S.Folder != null && S.Scan != null)
        {
            bool newScan = !ReferenceEquals(_shownRoot, S.Scan.Root);
            _shownRoot = S.Scan.Root;
            _shownFolder = S.Folder;
            Map.Show(S.Folder, S.Scan.FreeBytes, newScan ? MapTransition.Unfold : transition);
        }
        UpdateCrumbs();
        UpdateChips();
        UpdateFilterBar();
        UpdateDetails();
        UpdateSelectionUi();
        UpdateStatus(null);
    }

    private void ShowFolder()
    {
        var folder = S.Folder;
        var scan = S.Scan;
        if (folder == null || scan == null) return;
        var old = _shownFolder;
        _shownFolder = folder;
        PushSelectionToMap();
        if (old == null || !ReferenceEquals(old.Root, folder.Root))
        {
            Map.Show(folder, scan.FreeBytes, MapTransition.Fade);
        }
        else if (old.IsAncestorOf(folder))
        {
            // Zoom from wherever the new folder (or its nearest visible ancestor) sits in the old picture.
            DiskNode? anchor = null;
            var visible = Map.Boxes.SelectMany(b => b.Chain ?? (b.Node != null ? [b.Node] : [])).ToHashSet(ReferenceEqualityComparer.Instance);
            for (var n = folder; n != null && !ReferenceEquals(n, old); n = n.Parent)
            {
                if (visible.Contains(n)) { anchor = n; break; }
            }
            Map.Show(folder, scan.FreeBytes, MapTransition.ZoomIn, anchor ?? folder);
        }
        else if (folder.IsAncestorOf(old))
        {
            Map.Show(folder, scan.FreeBytes, MapTransition.ZoomOut);
        }
        else
        {
            Map.Show(folder, scan.FreeBytes, MapTransition.Fade);
        }
    }

    private void PushSelectionToMap()
    {
        Map.Selection = S.Selection;
        Map.FocusNode = S.Focus;
    }

    private void PushGrowthToMap()
    {
        var growth = S.Drive?.Growth;
        if (!S.ShowGrowth || growth == null)
        {
            Map.Growth = null;
            return;
        }
        var map = new Dictionary<DiskNode, long>(ReferenceEqualityComparer.Instance);
        foreach (var item in growth.Items)
        {
            if (item.Node != null && item.Node.Parent != null) map[item.Node] = item.Delta;
        }
        Map.Growth = map;
    }

    // ---- Header, chips, filter --------------------------------------------------------------

    private void UpdateCrumbs()
    {
        GrowthToggle.Visibility = S.Drive?.Growth is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _crumbNodes = new();
        for (var n = S.Folder; n != null; n = n.Parent) _crumbNodes.Insert(0, n);
        Crumbs.ItemsSource = _crumbNodes.Select(n => n.Parent == null ? $"{S.Drive?.Label} ({n.Name.TrimEnd('\\')})" : n.DisplayName).ToList();
        UpButton.IsEnabled = S.Folder?.Parent != null;
    }

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < _crumbNodes.Count) S.OpenFolder(_crumbNodes[args.Index]);
    }

    private void UpdateChips()
    {
        Chips.Children.Clear();
        var folder = S.Folder;
        if (folder == null) return;
        if (S.Settings.MapColors == MapColoring.Folder)
        {
            AddFolderChips(folder);
            return;
        }
        var totals = TreeOps.CategoryTotals(folder);
        foreach (var c in Categories.All.Where(c => totals[(int)c] > 0).OrderByDescending(c => totals[(int)c]))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(Dialogs.Swatch(c));
            content.Children.Add(new TextBlock { Text = Categories.Name(c) });
            content.Children.Add(Dialogs.SecondaryText(Format.Bytes(totals[(int)c])));
            var chip = new ToggleButton
            {
                Content = content,
                IsChecked = S.CategoryFilter == c,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(11, 4, 11, 5),
                FontSize = 12,
                Tag = c,
            };
            chip.Click += (s, _) =>
            {
                var cat = (Category)((FrameworkElement)s).Tag;
                S.SetFilter(S.Query, S.CategoryFilter == cat ? null : cat);
            };
            Chips.Children.Add(chip);
        }
    }

    /// <summary>When the map is coloured by folder, the chips name the coloured folders; clicking one opens it.</summary>
    private void AddFolderChips(DiskNode folder)
    {
        var groups = FolderGroups.Find(folder);
        for (int i = 0; i < groups.Count; i++)
        {
            var node = groups[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(MapColors.GroupSwatch(i)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock { Text = node.DisplayName });
            content.Children.Add(Dialogs.SecondaryText(Format.Bytes(node.Size)));
            var chip = new Button
            {
                Content = content,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(11, 4, 11, 5),
                FontSize = 12,
                Tag = node,
            };
            ToolTipService.SetToolTip(chip, node.FullPath);
            chip.Click += (s, _) => S.OpenFolder((DiskNode)((FrameworkElement)s).Tag);
            Chips.Children.Add(chip);
        }
    }

    private void UpdateFilterBar()
    {
        if (S.FilterHits == null)
        {
            FilterBar.Visibility = Visibility.Collapsed;
            return;
        }
        var what = S.CategoryFilter is { } c && string.IsNullOrWhiteSpace(S.Query) ? Categories.Name(c) : "matching items";
        FilterText.Text = $"{Format.Count(S.FilterMatches.Count)} {what.ToLowerInvariant()} · {Format.Bytes(S.FilterBytes)}";
        FilterBar.Visibility = Visibility.Visible;
    }

    private void OnSelectMatches(object sender, RoutedEventArgs e) => S.SelectMany(S.FilterMatches);

    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        App.Current?.Window?.ClearSearchBox();
        S.SetFilter("", null);
    }

    private void OnGrowthToggled(object sender, RoutedEventArgs e)
    {
        S.ShowGrowth = GrowthToggle.IsChecked == true;
        PushGrowthToMap();
        Map.InvalidateMap(fade: true);
    }

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        if (S.Drive != null) _ = S.ScanAsync(S.Drive);
    }

    private void OnUp(object sender, RoutedEventArgs e) => S.Up();

    // ---- Scan overlay and status ------------------------------------------------------------

    private void UpdateScanOverlay()
    {
        bool scanning = S.IsScanning;
        ScanOverlay.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        Map.Opacity = scanning ? 0.12 : 1;
        if (!scanning) return;
        ScanText.Text = S.ScanStatus;
        ScanBar.Visibility = S.ScanFraction >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (S.ScanFraction >= 0) ScanBar.Value = S.ScanFraction;
    }

    private void OnHoverChanged(object? sender, MapBox? box) => UpdateStatus(box);

    private void UpdateStatus(MapBox? box)
    {
        var scan = S.Scan;
        if (scan == null)
        {
            StatusText.Text = "";
            StatusRight.Text = "";
            return;
        }
        StatusRight.Text = $"{Format.Bytes(scan.FreeBytes)} free";
        if (box == null)
        {
            string how = scan.Method == ScanMethod.MasterFileTable
                ? $"Read the NTFS file table in {scan.Duration.TotalSeconds:0.0} s"
                : $"Walked the folders in {scan.Duration.TotalSeconds:0.0} s";
            string unreadable = scan.UnreadableFolders > 0 ? $" · {Format.Count(scan.UnreadableFolders)} folders couldn't be read" : "";
            StatusText.Text = $"{how} · {Format.Count(scan.ItemCount)} items{unreadable} · Area = space used. Double-click a folder to zoom in.";
            return;
        }
        double pct = scan.TotalBytes > 0 ? (double)box.Size / scan.TotalBytes : 0;
        StatusText.Text = box.Kind switch
        {
            BoxKind.Free => $"Free space · {Format.Bytes(box.Size)}",
            BoxKind.Rest => $"{Format.Count(box.RestCount)} small items · {Format.Bytes(box.Size)}",
            _ => $"{box.Node!.FullPath} · {Format.Bytes(box.Size)} · {Format.Percent(pct)} of {S.Drive?.Letter}",
        };
    }

    // ---- Map input --------------------------------------------------------------------------

    private void OnBoxClicked(object? sender, MapBoxEventArgs e)
    {
        if (e.Node == null) return;
        S.Click(e.Node, e.AddToSelection);
    }

    private void OnBoxDoubleClicked(object? sender, MapBoxEventArgs e)
    {
        var node = e.Node;
        if (node == null) return;
        if (node.IsDirectory && node.Children is { Count: > 0 }) S.OpenFolder(node);
        else if (node.Parent != null && !ReferenceEquals(node.Parent, S.Folder)) S.Reveal(node);
    }

    private void OnBoxRightClicked(object? sender, MapBoxEventArgs e)
    {
        var node = e.Node;
        if (node == null) return;
        if (!S.Selection.Contains(node)) S.Click(node, add: false);
        var menu = new MenuFlyout();
        if (node.IsDirectory && node.Children is { Count: > 0 })
            menu.Items.Add(Item("Zoom into folder", "", () => S.OpenFolder(node)));
        if (!node.IsSynthetic)
            menu.Items.Add(Item("Show in Explorer", "", () => ShellLauncher.ShowInExplorer(node.FullPath)));
        if (node.Tag is GameInfo game && (game.UninstallUri != null || game.UninstallCommand != null))
            menu.Items.Add(Item($"Uninstall with {game.Launcher}", "", () => Uninstall(game)));
        if (S.Selection.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var label = S.Selection.Count == 1 ? "Delete…" : $"Delete {S.Selection.Count:N0} items…";
            menu.Items.Add(Item(label, "", () => _ = DeleteAsync(S.Selection.ToList())));
        }
        if (menu.Items.Count == 0) return;
        var presenter = new Style(typeof(MenuFlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
        menu.MenuFlyoutPresenterStyle = presenter;
        menu.ShowAt(Map, new FlyoutShowOptions { Position = e.Position });
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Keyboard ---------------------------------------------------------------------------

    private bool TypingInTextBox => FocusManager.GetFocusedElement(XamlRoot) is TextBox;

    private void OnDeleteKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInTextBox) return;
        args.Handled = true;
        if (S.Selection.Count > 0) _ = DeleteAsync(S.Selection.ToList());
    }

    private void OnBackKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInTextBox) return;
        args.Handled = true;
        S.Up();
    }

    private void OnEscapeKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInTextBox) return;
        args.Handled = true;
        if (S.Selection.Count > 0) S.ClearSelection();
        else if (S.FilterHits != null) OnClearFilter(this, new RoutedEventArgs());
    }

    private void OnEnterKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInTextBox) return;
        if (S.Focus is { IsDirectory: true, Children.Count: > 0 } f)
        {
            args.Handled = true;
            S.OpenFolder(f);
        }
    }

    // ---- Selection and deleting -------------------------------------------------------------

    private void UpdateSelectionUi()
    {
        var top = TreeOps.TopLevel(S.Selection);
        bool any = top.Count > 0;
        DeleteButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearSelectionButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any)
        {
            DeleteText.Text = $"Delete {(top.Count == 1 ? "1 item" : $"{top.Count:N0} items")} · {Format.Bytes(top.Sum(n => n.Size))}";
        }
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) => S.ClearSelection();

    private void OnDeleteSelection(object sender, RoutedEventArgs e) => _ = DeleteAsync(S.Selection.ToList());

    private async Task DeleteAsync(IReadOnlyCollection<DiskNode> nodes)
    {
        if (nodes.Count == 0 || S.Drive == null) return;
        if (!await Dialogs.ConfirmDeleteAsync(XamlRoot, nodes, S.Settings.DeletePermanently, S.Drive.Letter)) return;
        int count = TreeOps.TopLevel(nodes).Count(n => n.CanDelete);
        var report = await S.DeleteAsync(nodes, count == 1 ? "Deleted 1 item" : $"Deleted {count:N0} items");
        await Dialogs.HandleFailuresAsync(XamlRoot, report);
    }

    private static void Uninstall(GameInfo game)
    {
        if (game.UninstallUri != null) ShellLauncher.Open(game.UninstallUri);
        else if (game.UninstallCommand != null) ShellLauncher.Start("cmd.exe", $"/c \"{game.UninstallCommand}\"");
    }

    private async Task RunSuggestionAsync(Suggestion s)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, s.ConfirmTitle, s.ConfirmBody, s.ActionLabel)) return;
        await S.RunSuggestionAsync(s);
    }

    // ---- Details pane -----------------------------------------------------------------------

    /// <summary>The colour a node has in the map: its folder group's when colouring by folder, else its file type's.</summary>
    private Brush SwatchBrush(DiskNode node)
    {
        if (!Map.ColorByFolder) return Dialogs.CategoryBrush(node.Category);
        var groups = Map.Groups;
        for (var n = node; n != null && !ReferenceEquals(n, Map.Folder); n = n.Parent)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (ReferenceEquals(groups[i], n)) return new SolidColorBrush(MapColors.GroupSwatch(i));
            }
        }
        return new SolidColorBrush(MapColors.NeutralSwatch);
    }

    private void UpdateDetails()
    {
        var top = TreeOps.TopLevel(S.Selection);
        object target = top.Count > 1 ? top.Count : (object?)(S.Focus ?? S.Folder) ?? "";
        Details.Children.Clear();
        if (top.Count > 1) BuildMultiDetails(top);
        else if ((S.Focus ?? S.Folder) is { } node) BuildNodeDetails(node);
        if (!Equals(target, _detailsFor)) Motion.Refresh(Details);
        _detailsFor = target;
    }

    private void BuildMultiDetails(List<DiskNode> nodes)
    {
        Details.Children.Add(new TextBlock { Text = $"{nodes.Count:N0} items selected", Style = Res<Style>("BodyStrongTextBlockStyle"), FontSize = 18 });
        Details.Children.Add(new TextBlock { Text = Format.Bytes(nodes.Sum(n => n.Size)), Style = Res<Style>("BigNumberStyle") });
        var list = new StackPanel { Spacing = 6 };
        foreach (var n in nodes.Take(8)) list.Children.Add(Dialogs.ItemRow(SwatchBrush(n), n.DisplayName, Format.Bytes(n.Size)));
        if (nodes.Count > 8) list.Children.Add(Caption($"and {nodes.Count - 8:N0} more"));
        Details.Children.Add(list);
        var actions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(ActionButton(S.Settings.DeletePermanently ? "Delete permanently" : "Move to Recycle Bin", "", accent: true,
            () => _ = DeleteAsync(nodes)));
        actions.Children.Add(ActionButton("Clear selection", null, accent: false, () => S.ClearSelection()));
        Details.Children.Add(actions);
    }

    private void BuildNodeDetails(DiskNode node)
    {
        var scan = S.Scan;
        if (scan == null) return;

        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(Dialogs.Swatch(SwatchBrush(node), 32, 6));
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = node.Parent == null ? S.Drive?.Title ?? node.Name : node.DisplayName,
            FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
        });
        titles.Children.Add(Caption($"{KindOf(node)} · {Categories.Name(node.Category)}"));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        Details.Children.Add(head);

        var sizeBlock = new StackPanel();
        sizeBlock.Children.Add(new TextBlock { Text = Format.Bytes(node.Size), Style = Res<Style>("BigNumberStyle") });
        double share = scan.TotalBytes > 0 ? (double)node.Size / scan.TotalBytes : 0;
        sizeBlock.Children.Add(Caption($"{Format.Percent(share)} of drive {S.Drive?.Letter}"));
        Details.Children.Add(sizeBlock);
        Details.Children.Add(new ProgressBar { Maximum = 1, Value = share, MinHeight = 4, CornerRadius = new CornerRadius(2) });

        var props = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Prop(string label, string value)
        {
            props.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Caption(label);
            Grid.SetRow(l, row);
            var v = new TextBlock { Text = value, Style = Res<Style>("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            props.Children.Add(l);
            props.Children.Add(v);
            row++;
        }
        if (!node.IsSynthetic) Prop("Location", node.FullPath);
        if (node.IsDirectory) Prop("Contains", $"{Format.Count(node.FileCount)} files");
        if (node.Modified != default) Prop("Modified", Format.Date(node.Modified));
        if (node.Logical > 0 && Math.Abs(node.Logical - node.Size) > Math.Max(64L << 20, node.Size / 10))
            Prop("File size", $"{Format.Bytes(node.Logical)} (uses {Format.Bytes(node.Size)} on disk)");
        if (node.Tag is GameInfo g && g.LastPlayed is { } played) Prop("Last played", Format.Date(played));
        if (row > 0) Details.Children.Add(props);

        if (NoteFor(node) is { } note)
        {
            var noteBox = new Border { Style = Res<Style>("NoteBorderStyle") };
            var np = new Grid { ColumnSpacing = 8 };
            np.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            np.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            np.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
            var nt = Caption(note);
            Grid.SetColumn(nt, 1);
            np.Children.Add(nt);
            noteBox.Child = np;
            Details.Children.Add(noteBox);
        }

        var kids = node.Children?.Where(c => c.Size > 0).Take(6).ToList();
        if (kids is { Count: > 0 })
        {
            var header = Dialogs.Caption("Largest inside");
            header.FontWeight = FontWeights.SemiBold;
            header.Margin = new Thickness(0, 4, 0, -6);
            Details.Children.Add(header);
            var list = new StackPanel();
            foreach (var child in kids)
            {
                var b = new Button
                {
                    Content = Dialogs.ItemRow(SwatchBrush(child), child.DisplayName, Format.Bytes(child.Size)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 5, 6, 5),
                    FontSize = 12,
                };
                var c = child;
                b.Click += (_, _) =>
                {
                    if (!ReferenceEquals(node, S.Folder) && node.Children != null) S.OpenFolder(node);
                    S.Click(c, add: false);
                };
                list.Children.Add(b);
            }
            Details.Children.Add(list);
        }

        var actions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        bool hasPrimary = false;
        if (node.Tag is GameInfo game && (game.UninstallUri != null || game.UninstallCommand != null))
        {
            bool viaLauncherPage = game.UninstallUri != null && !game.UninstallUri.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
            actions.Children.Add(ActionButton(viaLauncherPage ? $"Open {game.Launcher}" : $"Uninstall with {game.Launcher}", "", accent: true, () => Uninstall(game)));
            hasPrimary = true;
        }
        if (SuggestionFor(node) is { } suggestion)
        {
            actions.Children.Add(ActionButton(SuggestionLabel(suggestion), "", accent: !hasPrimary, () =>
            {
                if (suggestion.Kind == SuggestionKind.OpenSettings && suggestion.SettingsUri != null) ShellLauncher.Open(suggestion.SettingsUri);
                else _ = RunSuggestionAsync(suggestion);
            }, enabled: !suggestion.NeedsAdmin || S.IsElevated));
            hasPrimary = true;
        }
        if (node.CanDelete)
        {
            string label = S.Settings.DeletePermanently ? (hasPrimary ? "Delete folder instead" : "Delete permanently") : "Move to Recycle Bin";
            actions.Children.Add(ActionButton(label, "", accent: !hasPrimary, () => _ = DeleteAsync([node])));
        }
        if (node.IsDirectory && !ReferenceEquals(node, S.Folder) && node.Children is { Count: > 0 })
        {
            actions.Children.Add(ActionButton("Zoom into folder", "", accent: false, () => S.OpenFolder(node)));
        }
        if (!node.IsSynthetic)
        {
            actions.Children.Add(ActionButton("Show in Explorer", "", accent: false, () => ShellLauncher.ShowInExplorer(node.FullPath)));
        }
        Details.Children.Add(actions);
    }

    private static string SuggestionLabel(Suggestion s) => s.Key switch
    {
        "recycle" => "Empty Recycle Bin",
        "hibernate" => "Turn off hibernation",
        "winsxs" => "Run component cleanup",
        "restore" => "Remove older restore points",
        "windows-old" => "Open Storage settings",
        _ => $"{s.ActionLabel}: {s.Title.ToLowerInvariant()}",
    };

    private static Suggestion? SuggestionFor(DiskNode node)
    {
        var list = S.Drive?.Suggestions;
        if (list == null) return null;
        string? key = node.Name switch
        {
            "hiberfil.sys" when node.Parent?.Parent == null => "hibernate",
            "$Recycle.Bin" when node.Parent?.Parent == null => "recycle",
            "System Volume Information" when node.Parent?.Parent == null => "restore",
            "Windows.old" when node.Parent?.Parent == null => "windows-old",
            "Windows" when node.Parent?.Parent == null => "winsxs",
            "WinSxS" => "winsxs",
            _ => null,
        };
        if (key != null) return list.FirstOrDefault(s => s.Key == key);
        return list.FirstOrDefault(s => s.Kind == SuggestionKind.EmptyFolders && s.Nodes.Any(n => ReferenceEquals(n, node)));
    }

    private static string KindOf(DiskNode node)
    {
        if (node.Parent == null) return "Drive";
        if (node.IsSynthetic) return "System space";
        if ((node.Flags & NodeFlags.Game) != 0 && node.Tag is GameInfo g) return $"{g.Launcher} game";
        if ((node.Flags & NodeFlags.ReparsePoint) != 0) return "Link";
        return node.IsDirectory ? "Folder" : "File";
    }

    private static string? NoteFor(DiskNode node)
    {
        if (node.Tag is GameInfo g)
            return $"Installed with {g.Launcher}. Uninstalling there keeps your library in sync; deleting the folder works too, but {g.Launcher} will think it's still installed.";
        if ((node.Flags & NodeFlags.ReparsePoint) != 0)
            return "A link to another place. diskmap doesn't follow links, so it counts 0 bytes here and the target is counted where it really lives.";
        if (node.Parent?.Parent == null)
        {
            switch (node.Name)
            {
                case "hiberfil.sys": return "Holds the contents of memory while the PC hibernates. Turning hibernation off removes it (and Fast Startup).";
                case "pagefile.sys": return "Virtual memory. Windows manages its size; you can change it in Advanced system settings.";
                case "swapfile.sys": return "Used by Store apps when memory runs low. Managed by Windows.";
                case "$Recycle.Bin": return "Files you delete to the Recycle Bin still take space here until it's emptied.";
                case "System Volume Information": return "System Restore points and shadow copies.";
                case "NTFS metadata": return "The file system's own bookkeeping: the master file table, journals and security data. It can't be removed.";
                case "Hidden from the scan": return "Space Windows reports as used that the scan couldn't attribute to files, e.g. folders you don't have access to or space reserved by the system.";
            }
        }
        if (node.LinkedElsewhere > 64L << 20)
            return $"{Format.Bytes(node.LinkedElsewhere)} of files here also have a name in another folder (hardlinks). They're counted once, where they're used, so they don't show up twice.";
        if (node.IsProtected) return "Managed by Windows. diskmap won't delete it directly; use Clean up for the safe options.";
        if ((node.Flags & NodeFlags.Unreadable) != 0) return "diskmap couldn't read everything in this folder, so it may be bigger than shown.";
        if (node.Category == Category.Caches && node.IsDirectory) return "Cache or temporary data. Apps rebuild it when needed.";
        return null;
    }

    private static Button ActionButton(string text, string? glyph, bool accent, Action action, bool enabled = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph != null) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = enabled,
        };
        if (accent) b.Style = Res<Style>("AccentButtonStyle");
        if (!enabled) ToolTipService.SetToolTip(b, "Needs administrator rights");
        b.Click += (_, _) => action();
        return b;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Res<Style>("SecondaryCaptionStyle"),
    };

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
}
