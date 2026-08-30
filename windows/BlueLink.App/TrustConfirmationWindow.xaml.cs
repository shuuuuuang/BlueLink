using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueLink;

public sealed class TrustConfirmationWindow
{
    private readonly string _peerName;
    private readonly string _safetyCode;
    private readonly string _localFingerprint;
    private readonly string _remoteFingerprint;

    public Window? Owner { get; set; }

    public TrustConfirmationWindow(string peerName, string safetyCode,
        string localFingerprint, string remoteFingerprint)
    {
        _peerName = peerName;
        _safetyCode = safetyCode;
        _localFingerprint = localFingerprint;
        _remoteFingerprint = remoteFingerprint;
    }

    public bool? ShowDialog() => BlueLinkDialog.ConfirmContent(
        Owner,
        "确认安全连接",
        CreateContent(),
        primaryButtonText: "确认并信任",
        closeButtonText: "拒绝",
        tone: BlueLinkDialogTone.Information);

    private FrameworkElement CreateContent()
    {
        var content = new StackPanel
        {
            Width = 540,
            Margin = new Thickness(0, 6, 0, 2),
        };
        content.Children.Add(new TextBlock
        {
            Text = $"正在首次连接 {_peerName}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#162033"),
        });
        content.Children.Add(new TextBlock
        {
            Text = "请在两台设备上核对以下安全代码",
            Margin = new Thickness(0, 10, 0, 8),
            Foreground = Brush("#687386"),
        });
        content.Children.Add(new Border
        {
            Background = Brush("#EAF1FF"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 12, 18, 12),
            Child = new TextBlock
            {
                Text = _safetyCode,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#176BFF"),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        });
        content.Children.Add(FingerprintBlock("本机身份指纹", Group(_localFingerprint), 16));
        content.Children.Add(FingerprintBlock("对端身份指纹", Group(_remoteFingerprint), 10));
        content.Children.Add(new TextBlock
        {
            Text = "确认后会在本机固定该设备身份；密钥发生变化时连接将被拒绝。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#687386"),
            Margin = new Thickness(0, 16, 0, 0),
        });
        return content;
    }

    private static FrameworkElement FingerprintBlock(string title, string value, double topMargin)
    {
        var panel = new StackPanel { Margin = new Thickness(0, topMargin, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#162033"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Brush("#687386"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        return panel;
    }

    private static string Group(string value)
    {
        var normalized = new string(value.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        return string.Join(":", normalized.Chunk(4).Take(6).Select(chars => new string(chars)));
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
