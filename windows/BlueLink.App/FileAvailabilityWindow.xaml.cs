using System.Windows;
using System.Windows.Input;
using BlueLink.Domain;
using BlueLink.Localization;

namespace BlueLink;

public partial class FileAvailabilityWindow : Wpf.Ui.Controls.FluentWindow
{
    internal FileAvailabilityWindow(ChatAttachment attachment)
    {
        InitializeComponent();
        DataContext = attachment;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Strings.Language);
        ConstrainTo(SystemParameters.WorkArea.Size);
        Loaded += (_, _) => FileAvailabilityAcknowledgeButton.Focus();
    }

    internal static void Show(Window owner, ChatAttachment attachment)
    {
        var window = new FileAvailabilityWindow(attachment);
        if (owner.IsVisible)
        {
            window.Owner = owner;
            window.ConstrainTo(new Size(owner.ActualWidth, owner.ActualHeight));
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using var overlay = BlueLinkDialog.DimOwner(owner, "#47101828", owner is MainWindow ? 52 : 0);
        window.ShowDialog();
    }

    internal void ConstrainTo(Size available)
    {
        Width = Math.Min(520, Math.Max(1, available.Width - 48));
        MaxHeight = Math.Max(1, available.Height - 48);
        MinHeight = Math.Min(306, MaxHeight);
    }

    private void Close_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
