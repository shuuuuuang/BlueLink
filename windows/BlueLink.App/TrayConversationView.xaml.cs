using System.Windows;
using System.Windows.Controls;
using BlueLink.Notifications;

namespace BlueLink;
public partial class TrayConversationView : UserControl
{
    private readonly NotificationCenter _center;
    public event Action<string>? ConversationRequested;
    public event Action? DismissRequested;
    public TrayConversationView(NotificationCenter center)
    { InitializeComponent(); _center = center; DataContext = center; RefreshText(); }
    public void RefreshText() { Heading.Text = _center.Title; Subtitle.Text = _center.Subtitle; }
    private void Conversation_Click(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement { DataContext: NotificationConversation item }) ConversationRequested?.Invoke(item.PeerId); }
    private void Dismiss_Click(object sender, RoutedEventArgs args) => DismissRequested?.Invoke();
}
