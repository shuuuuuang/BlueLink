using BlueLink.Domain;

namespace BlueLink.Session;

internal static class TransferRetryEligibility
{
    public static bool Allows(TransferItem item, string? peerId, bool connected, bool trusted) =>
        connected && trusted && !string.IsNullOrWhiteSpace(peerId) &&
        string.Equals(item.PeerId, peerId, StringComparison.OrdinalIgnoreCase) &&
        item.Outgoing && item.Id != Guid.Empty && item.IsRetryableTerminal;
}
