using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NhlWorkbench.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "NHL Legacy Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var main = new MainWindow();
        main.Show();
        _ = main.StartAsync(e.Args);
    }
}

/// <summary>Visible when the value is false.</summary>
public sealed class HideIfConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when there's a value.</summary>
public sealed class ShowIfNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
