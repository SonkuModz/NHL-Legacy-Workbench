using System.Windows;
using System.Windows.Controls;
using NhlWorkbench.App.ViewModels;

namespace NhlWorkbench.App.Views;

public partial class TexturesView : UserControl
{
    // A tile is 128 wide, plus its ring and padding (8) and the gap after it (10).
    private const double TileWidth = 146;
    private const double ScrollBarRoom = 12;

    public TexturesView() => InitializeComponent();

    private TexturesViewModel? ViewModel => DataContext as TexturesViewModel;

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Columns = Math.Max(1, (int)((e.NewSize.Width - ScrollBarRoom) / TileWidth));
        }
    }

    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextureItem item })
        {
            ViewModel?.Need(item);
        }
    }

    private void OnTileUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextureItem item })
        {
            ViewModel?.NotNeeded(item);
        }
    }
}
