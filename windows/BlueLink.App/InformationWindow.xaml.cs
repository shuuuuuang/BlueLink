using System.Windows;
using System.Windows.Input;

namespace BlueLink;

internal sealed record InformationField(string Label, string Value, string Tone = "Normal")
{
    public override string ToString() => $"{Label}：{Value}";
}
internal sealed record InformationDocument(string Title, string SummaryTitle, string SummaryDetail,
    string IconSource, double Height, IReadOnlyList<InformationField> Fields, string Tone = "Normal");

public partial class InformationWindow : Wpf.Ui.Controls.FluentWindow
{
    internal InformationWindow(InformationDocument document)
    {
        InitializeComponent();
        DataContext = document;
        Height = document.Height;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.Strings.Language);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal static void Show(Window owner, InformationDocument document)
    {
        var window = new InformationWindow(document) { Owner = owner, MaxHeight = Math.Max(240, owner.ActualHeight - 80) };
        using var overlay = BlueLinkDialog.DimOwner(owner, "#3D0F172A", 52);
        window.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
