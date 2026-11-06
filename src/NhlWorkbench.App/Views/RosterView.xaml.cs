using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NhlWorkbench.App.ViewModels;

namespace NhlWorkbench.App.Views;

public partial class RosterView : UserControl
{
    public RosterView() => InitializeComponent();

    private RosterViewModel? ViewModel => DataContext as RosterViewModel;

    /// <summary>Enter saves a box, the same as leaving it.</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void OnEntryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RosterTeamEntry entry })
        {
            entry.PropertyChanged -= OnEntryChanged;
            entry.PropertyChanged += OnEntryChanged;
            NeedCrest(entry);
        }
    }

    private void OnEntryUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RosterTeamEntry entry })
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
        if (sender is RosterTeamEntry entry && e.PropertyName == nameof(RosterTeamEntry.Crest))
        {
            NeedCrest(entry);
        }
    }

    private void NeedCrest(RosterTeamEntry entry)
    {
        if (entry.Crest is { } crest)
        {
            ViewModel?.Textures.Need(crest);
        }
    }
}
