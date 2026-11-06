using System.Windows;

namespace NhlWorkbench.App.Views;

public partial class ThemedDialog : Window
{
    public ThemedDialog(string title, string message, string confirmText, string? initialAnswer, bool showCancel = true)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (initialAnswer is not null)
        {
            AnswerBox.Visibility = Visibility.Visible;
            AnswerBox.Text = initialAnswer;
            Loaded += (_, _) =>
            {
                AnswerBox.Focus();
                AnswerBox.SelectAll();
            };
        }
    }

    public string Answer => AnswerBox.Text;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
