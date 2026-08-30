using System.Windows;
using System.Windows.Media;

namespace BlueLink;

public enum BlueLinkDialogTone { Information, Warning, Error }

public partial class BlueLinkDialog : Window
{
    private BlueLinkDialog(Window? owner, string title, string message, bool confirmation, BlueLinkDialogTone tone)
    {
        InitializeComponent();
        if (owner is not null) Owner = owner;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        switch (tone)
        {
            case BlueLinkDialogTone.Warning:
                IconText.Text = "!"; IconText.Foreground = Brush("#B06A00"); IconSurface.Background = Brush("#FFF4E5"); break;
            case BlueLinkDialogTone.Error:
                IconText.Text = "×"; IconText.Foreground = Brush("#D92D20"); IconSurface.Background = Brush("#FDECEC"); break;
            default:
                IconText.Text = "i"; break;
        }
    }

    public static void Show(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Information) =>
        new BlueLinkDialog(owner, title, message, confirmation: false, tone).ShowDialog();

    public static bool Confirm(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Warning) =>
        new BlueLinkDialog(owner, title, message, confirmation: true, tone).ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
