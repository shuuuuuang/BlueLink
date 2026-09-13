using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Transport;

namespace BlueLink.Usb;

public static class UsbSessionPolicy
{
    // SessionSupervisor publishes Connected only after identity and trust verification.
    public static bool IsReady(bool enabled, string? peerId, IEnumerable<SessionSnapshot> sessions) =>
        enabled && !string.IsNullOrWhiteSpace(peerId) && sessions.Any(session =>
            session.Phase == ConnectionPhase.Connected && session.UsbFileReady &&
            string.Equals(session.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
    public static string? Notice(bool enabled, string? peerId, IEnumerable<SessionSnapshot> sessions, IEnumerable<UsbSnapshot> states)
    {
        var live = sessions.ToArray();
        if (!enabled || string.IsNullOrWhiteSpace(peerId) || IsReady(enabled, peerId, live)) return null;
        var state = states.LastOrDefault(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (state is null) return null;
        var bluetooth = live.Any(value => value.Phase == ConnectionPhase.Connected && value.Transport == TransportKind.Bluetooth &&
            string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        return state.Stage switch
        {
            UsbStage.Authorization => "请在手机上允许蓝联使用 USB 连接。",
            UsbStage.Negotiating => "正在验证 USB 连接；原有会话可继续使用。",
            UsbStage.Fallback or UsbStage.Unavailable => bluetooth ? "USB 连接已断开，新传输将使用蓝牙。" : "USB 连接已断开，设备当前离线。",
            UsbStage.PermissionDenied or UsbStage.DriverMissing or UsbStage.PolicyBlocked or UsbStage.Unsupported => "USB 暂不可用，请在帮助中查看连接指引。",
            _ => null
        };
    }
}

