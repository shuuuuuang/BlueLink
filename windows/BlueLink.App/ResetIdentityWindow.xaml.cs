using System.Windows;
using System.Windows.Input;

namespace BlueLink;

public partial class ResetIdentityWindow : Wpf.Ui.Controls.FluentWindow
{
    public ResetIdentityWindow() => InitializeComponent();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Reset_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
    }
}
