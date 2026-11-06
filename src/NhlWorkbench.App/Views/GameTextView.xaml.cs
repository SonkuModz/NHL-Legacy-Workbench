using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace NhlWorkbench.App.Views;

public partial class GameTextView : UserControl
{
    public GameTextView() => InitializeComponent();

    /// <summary>Enter saves the box, the same as leaving it.</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }
}
