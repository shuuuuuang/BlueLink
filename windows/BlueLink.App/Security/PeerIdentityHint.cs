using System.Text.RegularExpressions;

namespace BlueLink.Security;

// A hint selects a confirmation candidate. It never authenticates a peer.
public static class PeerIdentityHint
{
    public static string? Bluetooth(string? address)
    {
        var value = (address ?? "").Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
        return Regex.IsMatch(value, "^[0-9A-F]{12}$") && value is not ("000000000000" or "FFFFFFFFFFFF" or "020000000000")
            ? "bt:" + value : null;
    }
    public static string? UsbSerial(string? serial)
    {
        var value = serial?.Trim();
        return value is { Length: >= 4 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
            value.Any(c => c != '0') ? "usb-serial:" + value.ToUpperInvariant() : null;
    }
}

public sealed record IdentityAssociationHandler(
    Func<string, CancellationToken, Task<IdentityCandidate?>> Find,
    Func<IdentityCandidate, bool> CanAssociate,
    Func<Task> Apply);

public sealed record IdentityCandidate(string PeerId, string DisplayName, byte[]? PublicKey, string Hint);
