using System.Windows;

namespace BlueLink;

public partial class TrustConfirmationWindow : Window
{
    public TrustConfirmationWindow(string peerName, string safetyCode,
        string localFingerprint, string remoteFingerprint)
    {
        InitializeComponent();
        PeerNameText.Text = $"正在首次连接 {peerName}";
        SafetyCodeText.Text = safetyCode;
        LocalFingerprintText.Text = Group(localFingerprint);
        RemoteFingerprintText.Text = Group(remoteFingerprint);
    }

    private static string Group(string value)
    {
        var normalized = new string(value.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        return string.Join(":", normalized.Chunk(4).Take(6).Select(chars => new string(chars)));
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Reject_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
