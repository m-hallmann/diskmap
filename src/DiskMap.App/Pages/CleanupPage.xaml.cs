using DiskMap.Core;
using DiskMap.Core.Actions;
using DiskMap.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.Pages;

public sealed partial class CleanupPage : Page
{
    private static AppState S => AppState.Current;
    private bool _busy;

    public CleanupPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            S.Changed += OnStateChanged;
            Build();
        };
        Unloaded += (_, _) => S.Changed -= OnStateChanged;
    }

    private void OnStateChanged(StateChange change)
    {
        if (change is StateChange.Scanned or StateChange.Tree) Build();
    }

    private void Build()
    {
        Cards.Children.Clear();
        var drive = S.Drive;
        var list = drive?.Suggestions ?? new();
        Subtitle.Text = drive == null ? "" : $"Ways to free space on {drive.Letter}, biggest first. System items use Windows' own tools.";
        long total = list.Where(s => s.Kind != SuggestionKind.Review).Sum(s => s.Bytes);
        TotalText.Text = total > 0 ? $"Up to {Format.Bytes(total)} available" : "";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = S.IsScanning ? "Waiting for the scan to finish." : $"Nothing obvious to clean up on {drive?.Letter}. Use the map to find big folders.";
        foreach (var s in list) Cards.Children.Add(Card(s));
    }

    private Border Card(Suggestion s)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

        var icon = new Border
        {
            Style = Dialogs.AppStyle("IconTileStyle"),
            Child = Dialogs.Swatch(s.Category, 16, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ((FrameworkElement)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = s.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = s.Description, Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"] });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var size = new TextBlock
        {
            Text = (s.SizeIsEstimate ? "up to " : "") + Format.Bytes(s.Bytes),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (s.SizeIsEstimate) size.FontSize = 16;
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        bool allowed = !s.NeedsAdmin || S.IsElevated;
        var button = new Button
        {
            Content = s.ActionLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = allowed,
        };
        if (!allowed) ToolTipService.SetToolTip(button, "Needs administrator rights. Restart diskmap as administrator.");
        button.Click += async (_, _) => await RunAsync(s);
        Grid.SetColumn(button, 3);
        grid.Children.Add(button);

        return new Border { Style = (Style)Application.Current.Resources["SettingsCardStyle"], Child = grid };
    }

    private async Task RunAsync(Suggestion s)
    {
        if (_busy) return;
        switch (s.Kind)
        {
            case SuggestionKind.Review:
                if (s.ReviewFolder != null)
                {
                    S.OpenFolder(s.ReviewFolder);
                    S.SetFilter(s.ReviewQuery ?? "", s.ReviewCategory);
                    App.Current?.Window?.NavigateTo("map");
                }
                return;
            case SuggestionKind.OpenSettings:
                if (s.SettingsUri != null) ShellLauncher.Open(s.SettingsUri);
                return;
        }
        if (!await Dialogs.ConfirmAsync(XamlRoot, s.ConfirmTitle, s.ConfirmBody, s.ActionLabel)) return;
        _busy = true;
        try
        {
            await S.RunSuggestionAsync(s);
        }
        finally
        {
            _busy = false;
        }
    }
}
