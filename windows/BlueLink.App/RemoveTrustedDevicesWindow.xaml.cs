using System.Windows;
using System.Windows.Input;
using BlueLink.Domain;

namespace BlueLink;

public partial class RemoveTrustedDevicesWindow : Wpf.Ui.Controls.FluentWindow
{
    public IReadOnlyList<ConversationSummary> Peers { get; }
    public string Description => Localization.Strings.Format($"将从本机可信列表中移除以下 {Peers.Count} 台设备。");

    public RemoveTrustedDevicesWindow(IEnumerable<ConversationSummary> peers)
    {
        Peers = peers.ToArray();
        InitializeComponent();
        DataContext = this;
        RemoveTrustConfirmButton.IsEnabled = Peers.Count > 0;
        Loaded += (_, _) => RemoveTrustCancelButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Peers.Count > 0) DialogResult = true; }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
    }
}
