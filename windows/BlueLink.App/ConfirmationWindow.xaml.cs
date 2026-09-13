using System.Windows;
using System.Windows.Input;
using BlueLink.Localization;

namespace BlueLink;

internal sealed record ConfirmationDocument(string Title, string Question, string Impact, string PrimaryText, double PrimaryMinWidth = 84,
    bool Destructive = true, string? CancelText = null, string? SecondaryText = null, BlueLinkDialogTone? Tone = null)
{
    internal static ConfirmationDocument RemoveTrust(string name) => new(Strings.Get("移除信任"),
        Strings.Format($"确定移除对 {name} 的信任吗？"), Strings.Get("下次连接时需要重新核对安全码并授权。"), Strings.Get("移除"));
    internal static ConfirmationDocument ClearConversation(string name) => new(Strings.Get("清除会话记录"),
        Strings.Format($"确定清除与 {name} 的全部会话记录吗？"), Strings.Get("只删除本机记录，已保存的文件不会被删除。"), Strings.Get("清除"));
    internal static ConfirmationDocument DeleteRecord(string question) => new(Strings.Get("删除本机记录"),
        question, Strings.Get("只删除本机记录，已保存的文件不会被删除。"), Strings.Get("删除"));
    internal static ConfirmationDocument CancelTransfer(string name, bool outgoing) => new(Strings.Get(outgoing ? "取消传输" : "取消接收"),
        outgoing ? Strings.Format($"确定取消“{name}”的传输吗？") : Strings.Format($"确定取消“{name}”的接收吗？"),
        Strings.Get("取消后本次任务会停止，已完成的文件不会被删除。"), Strings.Get(outgoing ? "取消传输" : "取消接收"), 96);
    internal static ConfirmationDocument ClearAll(bool files) => new(Strings.Get(files ? "清空全部文件记录" : "清空全部会话记录"),
        Strings.Get(files ? "确定清空所有文件传输与接收记录吗？" : "确定清空与所有设备的全部会话记录吗？"),
        Strings.Get("仅删除记录，不会删除 Download 中已接收的文件。"), Strings.Get("清空"));
}

public partial class ConfirmationWindow : Wpf.Ui.Controls.FluentWindow
{
    internal bool Confirmed { get; private set; }
    internal Wpf.Ui.Controls.MessageBoxResult Result { get; private set; } = Wpf.Ui.Controls.MessageBoxResult.None;
    internal ConfirmationWindow(ConfirmationDocument document, UIElement? options = null)
    {
        InitializeComponent();
        DataContext = document;
        Title = document.Title;
        ConfirmationOptions.Content = options;
        if (string.IsNullOrWhiteSpace(document.Question)) ConfirmationMessageRow.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(document.Impact)) ConfirmationImpactTile.Visibility = Visibility.Collapsed;
        if (document.CancelText is not null) ConfirmationCancelButton.Content = document.CancelText;
        if (document.CancelText == string.Empty) ConfirmationCancelButton.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(document.SecondaryText)) ConfirmationSecondaryButton.Visibility = Visibility.Visible;
        if (!document.Destructive)
        {
            ConfirmationPrimaryButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            ConfirmationPrimaryButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            ConfirmationPrimaryButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "PrimaryBrush");
            ConfirmationIconTile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SoftBlueBrush");
            ConfirmationImpactTile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SoftBlueBrush");
            ConfirmationImpact.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
            var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Document20, FontSize = 20 };
            icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "BlueBrush");
            ConfirmationIconTile.Child = icon;
        }
        if (document.Tone is { } tone)
        {
            var color = tone == BlueLinkDialogTone.Error ? "DangerBrush" : tone == BlueLinkDialogTone.Warning ? "Text8A5700Brush" : "BlueBrush";
            var background = tone == BlueLinkDialogTone.Error ? "PanelFFF1F0Brush" : tone == BlueLinkDialogTone.Warning ? "ConfirmationImpactBrush" : "SoftBlueBrush";
            var icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = tone == BlueLinkDialogTone.Error ? Wpf.Ui.Controls.SymbolRegular.DismissCircle20 :
                    tone == BlueLinkDialogTone.Warning ? Wpf.Ui.Controls.SymbolRegular.Warning20 : Wpf.Ui.Controls.SymbolRegular.Info20,
                FontSize = 20, Width = 24, Height = 24,
            };
            icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, color);
            ConfirmationIconTile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, background);
            ConfirmationIconTile.Child = icon;
        }
        var primaryColor = document.Destructive ? "ConfirmationDangerBrush" : "PrimaryBrush";
        ConfirmationPrimaryButton.SetResourceReference(Wpf.Ui.Controls.Button.MouseOverBackgroundProperty, primaryColor);
        ConfirmationPrimaryButton.SetResourceReference(Wpf.Ui.Controls.Button.PressedBackgroundProperty, primaryColor);
        ConfirmationPrimaryButton.SetResourceReference(Wpf.Ui.Controls.Button.PressedForegroundProperty, "OnAccentBrush");
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Strings.Language);
        MaxHeight = Math.Max(250, SystemParameters.WorkArea.Height - 40);
        Loaded += (_, _) => { if (ShowActivated) (ConfirmationCancelButton.IsVisible ? ConfirmationCancelButton : ConfirmationPrimaryButton).Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    internal static bool Show(Window owner, ConfirmationDocument document)
    {
        var window = new ConfirmationWindow(document) { Owner = owner, MaxHeight = Math.Max(250, owner.ActualHeight - 80) };
        using var overlay = BlueLinkDialog.DimOwner(owner, "#3D0F172A", owner is MainWindow ? 52 : 0);
        window.ShowDialog();
        return window.Confirmed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Confirm_Click(object sender, RoutedEventArgs e) { Confirmed = true; Result = Wpf.Ui.Controls.MessageBoxResult.Primary; Close(); }
    private void Secondary_Click(object sender, RoutedEventArgs e) { Result = Wpf.Ui.Controls.MessageBoxResult.Secondary; Close(); }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
