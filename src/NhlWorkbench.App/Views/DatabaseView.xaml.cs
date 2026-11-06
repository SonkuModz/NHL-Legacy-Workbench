using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NhlWorkbench.App.ViewModels;
using NhlWorkbench.Core.Database;

namespace NhlWorkbench.App.Views;

public partial class DatabaseView : UserControl
{
    private DatabaseViewModel? _viewModel;

    public DatabaseView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as DatabaseViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ColumnsChanged += OnColumnsChanged;
            _viewModel.CellWanted += OnCellWanted;
            BuildColumns();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ColumnsChanged -= OnColumnsChanged;
            _viewModel.CellWanted -= OnCellWanted;
        }
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildColumns();

    private void OnCellWanted(object? sender, (DatabaseRow Row, int Column) cell)
    {
        string path = cell.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Grid.UpdateLayout();
        Grid.ScrollIntoView(cell.Row, Grid.Columns.FirstOrDefault(c => c.SortMemberPath == path));
    }

    /// <summary>A grid's columns can't be bound, and every table has its own, so they're made here.</summary>
    private void BuildColumns()
    {
        Grid.Columns.Clear();
        if (_viewModel?.CurrentTable is not { } table)
        {
            return;
        }

        var cellStyle = (Style)FindResource("DataCellText");
        var editStyle = (Style)FindResource("DataCellEdit");
        var changedTint = new SolidColorBrush(((SolidColorBrush)FindResource("Caution")).Color) { Opacity = 0.24 };
        changedTint.Freeze();
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding(nameof(DatabaseRow.Number)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            CanUserSort = false,
            Width = 54,
            ElementStyle = (Style)FindResource("DataCellNumber"),
        });

        // Names come first, so a row says what it is without scrolling across. Each column still reads its own cell.
        foreach (int i in Enumerable.Range(0, table.Columns.Count).OrderBy(c => table.Columns[c].IsText ? 0 : 1))
        {
            DatabaseColumn column = table.Columns[i];
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = column.Name, ToolTip = $"{column.Name} ({column.Id})\n{column.Hint}" },
                Binding = new Binding($"[{i}]") { Mode = column.IsEditable ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                IsReadOnly = !column.IsEditable,
                SortMemberPath = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = column.IsText ? Math.Max(150, (column.Name.Length * 8) + 28) : Math.Max(64, (column.Name.Length * 8) + 28),
                ElementStyle = cellStyle,
                EditingElementStyle = editStyle,
                CellStyle = ChangedCellStyle(i, changedTint),
            });
        }

        Grid.FrozenColumnCount = 1;
        Grid.ScrollIntoView(Grid.Items.Count > 0 ? Grid.Items[0] : null);
    }

    /// <summary>Tints a cell that holds something different from the game's copy.</summary>
    private Style ChangedCellStyle(int column, Brush tint)
    {
        var style = new Style(typeof(DataGridCell), Grid.CellStyle);
        var changed = new DataTrigger { Binding = new Binding($"Changed[{column}]"), Value = true };
        changed.Setters.Add(new Setter(BackgroundProperty, tint));
        style.Triggers.Add(changed);
        return style;
    }

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_viewModel?.CanBeginEdit() != true)
        {
            e.Cancel = true;
        }
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || !int.TryParse(e.Column.SortMemberPath, out int index))
        {
            return;
        }

        bool descending = _viewModel.SortBy(index);
        foreach (DataGridColumn column in Grid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }
}
