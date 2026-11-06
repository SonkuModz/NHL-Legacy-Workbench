using System.Windows;
using System.Windows.Controls;
using NhlWorkbench.App.ViewModels;

namespace NhlWorkbench.App.Views;

public partial class TeamsView : UserControl
{
    // A tile is 128 wide, plus its ring and padding (8) and the gap after it (10).
    private const double TileWidth = 146;
    private const double ScrollBarRoom = 12;

    public TeamsView() => InitializeComponent();

    private TeamsViewModel? ViewModel => DataContext as TeamsViewModel;

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Columns = Math.Max(1, (int)((e.NewSize.Width - ScrollBarRoom) / TileWidth));
        }
    }

    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamTile tile })
        {
            ViewModel?.Textures.Need(tile.Item);
        }
    }

    private void OnTileUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamTile tile })
        {
            ViewModel?.Textures.NotNeeded(tile.Item);
        }
    }

    private void OnEntryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamEntry entry })
        {
            entry.PropertyChanged -= OnEntryChanged;
            entry.PropertyChanged += OnEntryChanged;
            NeedCrest(entry);
        }
    }

    private void OnEntryUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamEntry entry })
        {
            entry.PropertyChanged -= OnEntryChanged;
            if (entry.Crest is { } crest)
            {
                ViewModel?.Textures.NotNeeded(crest);
            }
        }
    }

    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is TeamEntry entry && e.PropertyName == nameof(TeamEntry.Crest))
        {
            NeedCrest(entry);
        }
    }

    private void NeedCrest(TeamEntry entry)
    {
        if (entry.Crest is { } crest)
        {
            ViewModel?.Textures.Need(crest);
        }
    }
}
