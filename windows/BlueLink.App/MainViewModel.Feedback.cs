using BlueLink.Domain;
using BlueLink.Session;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, DateTimeOffset> _automaticFailureNotices = new(StringComparer.OrdinalIgnoreCase);
    private bool _dialCanceledByUser;
    internal event Action<string, ToastLevel>? TransientNoticeRequested;

    internal void ReportScanFailure(Exception error)
    {
        SessionLog.Write("Discovery", $"Bluetooth scan failed: {error}");
        ScanFeedback = Localization.Strings.Get(error is TimeoutException or OperationCanceledException
            ? "扫描超时，请重试。" : "扫描失败，请确认蓝牙可用后重试。");
        if (_disposeStarted == 0) TransientNoticeRequested?.Invoke(ScanFeedback, ToastLevel.Error);
    }

    internal void ReportConnectionFailure(NearbyDevice device, Exception error, bool automatic, DateTimeOffset? now = null)
    {
        if (_disposeStarted != 0 || _resettingIdentity || IsBluetoothUnavailable || _dialCanceledByUser) return;
        SessionLog.Write("Connection", "Bluetooth connection attempt failed", error);
        var address = NormalizeAddress(device.Address);
        var occurredAt = now ?? DateTimeOffset.UtcNow;
        if (automatic && _automaticFailureNotices.TryGetValue(address, out var previous) &&
            occurredAt - previous < TimeSpan.FromMinutes(1)) return;
        if (automatic) _automaticFailureNotices[address] = occurredAt;
        var message = error is TimeoutException or OperationCanceledException
            ? Localization.Strings.Format($"连接 {device.Name} 超时，请确认对方已打开蓝联后重试。")
            : Localization.Strings.Format($"无法连接 {device.Name}，请确认对方蓝牙和蓝联已开启后重试。");
        TransientNoticeRequested?.Invoke(message, ToastLevel.Error);
    }
}
