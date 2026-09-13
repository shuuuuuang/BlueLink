using BlueLink.Domain;

namespace BlueLink.Transport;

public enum TransportKind { Bluetooth, Usb }

public interface IPeerConnection : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    string PeerName { get; }
    string TransportAddress { get; }
    string? IdentityHint => Transport == TransportKind.Bluetooth ? Security.PeerIdentityHint.Bluetooth(TransportAddress) : null;
    bool ListenerRole { get; }
    TransportKind Transport { get; }
    PeerPlatform Platform { get; }
}
