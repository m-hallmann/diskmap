using DiskMap.Core;
using DiskMap.Core.Actions;
using DiskMap.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DiskMap.Services;

internal static class Dialogs
{
    public static SolidColorBrush CategoryBrush(Category c)
    {
        uint rgb = Categories.Rgb(c);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    public static FrameworkElement Swatch(Category c, double size = 10, double radius = 3) => Swatch(CategoryBrush(c), size, radius);

    public static FrameworkElement Swatch(Brush brush, double size = 10, double radius = 3) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(radius),
        Background = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Grid ItemRow(Category c, string name, string size) => ItemRow(CategoryBrush(c), name, size);

    public static Grid ItemRow(Brush swatch, string name, string size)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Swatch(swatch));
        var nameText = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 1);
        g.Children.Add(nameText);
        var sizeText = SecondaryText(size);
        sizeText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sizeText, 2);
        g.Children.Add(sizeText);
        return g;
    }

    /// <summary>Secondary-coloured text that follows the theme of wherever it is shown.</summary>
    public static TextBlock SecondaryText(string text) => new() { Text = text, Style = AppStyle("SecondaryTextStyle") };

    public static TextBlock Caption(string text) => new() { Text = text, Style = AppStyle("SecondaryCaptionStyle") };

    public static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Dialogs and flyouts live outside the window's visual tree, so they need the theme set explicitly.</summary>
    public static ElementTheme ThemeOf(XamlRoot root) => (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;

    private static ContentDialog NewDialog(XamlRoot root) => new()
    {
        XamlRoot = root,
        RequestedTheme = ThemeOf(root),
        Style = AppStyle("DefaultContentDialogStyle"),
    };

    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, IReadOnlyCollection<DiskNode> nodes, bool permanently, string driveLetter)
    {
        var top = TreeOps.TopLevel(nodes).Where(n => n.CanDelete).ToList();
        if (top.Count == 0) return false;
        long total = top.Sum(n => n.Size);

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        var intro = SecondaryText(permanently
            ? "They skip the Recycle Bin and can't be restored."
            : "They go to the Recycle Bin. The space is freed once you empty it.");
        intro.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(intro);
        var list = new StackPanel { Spacing = 6 };
        foreach (var n in top.Take(5)) list.Children.Add(ItemRow(n.Category, n.DisplayName, Format.Bytes(n.Size)));
        if (top.Count > 5) list.Children.Add(Caption($"and {top.Count - 5:N0} more"));
        panel.Children.Add(list);
        panel.Children.Add(new Border { Style = AppStyle("DividerStyle") });
        var sum = new Grid();
        sum.Children.Add(new TextBlock { Text = permanently ? $"Space freed on {driveLetter}" : "Moved to the Recycle Bin", VerticalAlignment = VerticalAlignment.Center });
        sum.Children.Add(new TextBlock { Text = Format.Bytes(total), HorizontalAlignment = HorizontalAlignment.Right, Style = AppStyle("SubtitleTextBlockStyle") });
        panel.Children.Add(sum);

        var dialog = NewDialog(root);
        dialog.Title = top.Count == 1
            ? $"Delete “{top[0].DisplayName}”{(permanently ? " permanently" : "")}?"
            : $"{(permanently ? "Permanently delete" : "Delete")} {top.Count:N0} items?";
        dialog.Content = panel;
        dialog.PrimaryButtonText = permanently ? "Delete permanently" : "Move to Recycle Bin";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string body, string verb)
    {
        var dialog = NewDialog(root);
        dialog.Title = title;
        dialog.Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        dialog.PrimaryButtonText = verb;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Follows the "Files that are in use" setting after a delete with failures.</summary>
    public static async Task HandleFailuresAsync(XamlRoot root, DeleteReport? report)
    {
        if (report == null || report.Succeeded) return;
        var state = AppState.Current;
        switch (state.Settings.LockedFiles)
        {
            case Core.Settings.LockedFilePolicy.DeleteAtRestart when state.IsElevated:
                int n = await Task.Run(() => DeleteService.DeleteAtRestart(report.Failures.Select(f => f.Path).Distinct()));
                state.ShowNotice(new Notice("Some items are in use", $"{n:N0} items will be deleted the next time Windows starts.", InfoBarSeverity.Informational));
                break;
            case Core.Settings.LockedFilePolicy.Skip:
                break;
            default:
                await ShowFailuresAsync(root, report, state.IsElevated);
                break;
        }
    }

    /// <summary>Lists what couldn't be deleted and offers to finish the job at the next restart.</summary>
    public static async Task ShowFailuresAsync(XamlRoot root, DeleteReport report, bool canScheduleAtRestart)
    {
        var failures = report.Failures.ToList();
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        var intro = SecondaryText("These items are open in another app or protected. Close the app and try again, or delete them when Windows restarts.");
        intro.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(intro);
        foreach (var f in failures.Take(6))
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = Path.GetFileName(f.Path.TrimEnd('\\')), TextTrimming = TextTrimming.CharacterEllipsis });
            string detail = f.LockedBy.Count > 0 ? $"{f.Reason}: {string.Join(", ", f.LockedBy)}" : f.Reason;
            var d = Caption(detail);
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(d);
            panel.Children.Add(row);
        }
        if (failures.Count > 6) panel.Children.Add(Caption($"and {failures.Count - 6:N0} more"));

        var dialog = NewDialog(root);
        dialog.Title = "Some items couldn't be deleted";
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 360 };
        dialog.CloseButtonText = "Close";
        if (canScheduleAtRestart)
        {
            dialog.PrimaryButtonText = "Delete when Windows restarts";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            int n = await Task.Run(() => DeleteService.DeleteAtRestart(failures.Select(f => f.Path).Distinct()));
            AppState.Current.ShowNotice(new Notice("Scheduled for restart", $"{n:N0} items will be deleted the next time Windows starts.",
                InfoBarSeverity.Informational));
        }
    }
}
