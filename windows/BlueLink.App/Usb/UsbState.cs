using BlueLink.Localization;

namespace BlueLink.Usb;

public enum UsbStage { Off, Waiting, Authorization, PermissionDenied, Negotiating, Ready, HighSpeed, FullSpeed, Fallback, Unavailable, DriverMissing, PolicyBlocked, Unsupported }
public enum UsbLinkSpeed { Unknown, FullSpeed = 2, HighSpeed = 3 }
public sealed record UsbSnapshot(UsbStage Stage = UsbStage.Off, string PeerName = "", string DeviceId = "",
    string Driver = "", UsbLinkSpeed Speed = UsbLinkSpeed.Unknown, bool BluetoothAvailable = false, string? Error = null)
{
    // Filled only by an authenticated session, never by USB descriptors or ADB enumeration.
    public string? PeerId { get; init; }
    public string Channel => Stage switch
    {
        UsbStage.Ready => "USB", UsbStage.HighSpeed => Speed == UsbLinkSpeed.Unknown ? "USB" : "USB · High-Speed", UsbStage.FullSpeed => "USB · Full-Speed",
        UsbStage.Fallback => "Bluetooth RFCOMM",
        _ => BluetoothAvailable ? "Bluetooth RFCOMM" : Strings.Get("暂无可用通道"),
    };
    public bool IsReady => Stage is UsbStage.Ready or UsbStage.HighSpeed or UsbStage.FullSpeed;
}

public sealed record UsbDevice(string InstanceId, string Name, string InterfacePath, string Driver,
    bool AccessoryMode, bool AndroidCandidate, uint ProblemCode = 0, bool PolicyBlocked = false);
public sealed class UsbFailure(UsbStage stage, string message, Exception? inner = null) : IOException(message, inner)
{
    public UsbStage Stage { get; } = stage;
}
