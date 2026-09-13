using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;
using BlueLink.Storage;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private static string InformationText(string? value) => string.IsNullOrWhiteSpace(value) ? Strings.Get("未记录") : value;
    private static string InformationTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Strings.Culture) ?? Strings.Get("未记录");
    private static InformationField Field(string label, string? value, string tone = "Normal") => new(Strings.Get(label), InformationText(value), tone);
    private static string InformationIcon(string name) => "/BlueLink;component/Assets/Figma/" + name + ".png";

    internal InformationDocument DescribeDevice(ConversationSummary peer)
    {
        var trusted = _identity.TrustedIdentities.Any(item => string.Equals(item.PeerIdHex, peer.PeerId, StringComparison.OrdinalIgnoreCase));
        var signal = Devices.FirstOrDefault(item => item.Address == peer.TransportAddress)?.Rssi;
        var state = Strings.Get(peer.IsConnected ? "已连接" : peer.IsOffline ? "离线" : "附近可连接");
        return DeviceInformation(peer.PeerName, peer.PlatformText, peer.PeerId, peer.PlatformIconSource,
            trusted, state, peer.IsConnected, signal, peer.LastConnectedAt);
    }

    internal InformationDocument DescribeDevice(NearbyDevice device)
    {
        var peer = Conversations.FirstOrDefault(item => item.TransportAddress == device.Address);
        if (peer is not null) return DescribeDevice(peer);
        var platform = device.Platform == PeerPlatform.Unknown ? "BlueLink" : device.Platform.ToString();
        return DeviceInformation(device.Name, platform, device.Address,
            InformationIcon(device.Platform == PeerPlatform.Android ? "phone" : device.Platform == PeerPlatform.Windows ? "desktop" : "generic"),
            false, Strings.Get(device.IsConnecting ? "连接中" : device.HasConnectionFailure ? "连接失败" : "未连接"), false, device.Rssi, null);
    }

    private static InformationDocument DeviceInformation(string name, string platform, string identifier, string icon,
        bool trusted, string state, bool connected, short? rssi, DateTimeOffset? lastConnected) => new(
        Strings.Get("设备详情"), name, $"{platform} · {state}", icon, 400,
        [Field("设备名称", name), Field("设备类型", Strings.Format($"{platform} 设备")), Field("设备标识", identifier),
         Field("信任状态", Strings.Get(trusted ? "已信任" : "未信任"), trusted ? "Success" : "Normal"),
         Field("连接状态", state, connected ? "Success" : "Normal"), Field("蓝牙信号", rssi is { } value ? $"{value} dBm" : null),
         Field("最后连接", InformationTime(lastConnected))]);

    internal async Task<InformationDocument> DescribeAttachmentAsync(ChatAttachment attachment, TransferItem? transfer = null, bool failure = false)
    {
        transfer ??= AllTransfers.FirstOrDefault(item => item.Id == attachment.TransferId);
        var stored = (await _database.LoadTransfersAsync(transfer?.PeerId, _lifetime.Token))
            .FirstOrDefault(item => Guid.TryParse(item.TransferId, out var id) && id == attachment.TransferId);
        var message = Messages.FirstOrDefault(item => item.Attachments?.Any(value => value.TransferId == attachment.TransferId) == true);
        bool? outgoing = transfer?.Outgoing ?? (stored is null ? message?.Outgoing : stored.Direction == "Outgoing");
        var peerId = transfer?.PeerId ?? stored?.PeerId;
        var peerName = peerId is null ? transfer?.PeerName : Conversations.FirstOrDefault(item => item.PeerId == peerId)?.PeerName
            ?? _storedPeers.GetValueOrDefault(peerId)?.DisplayName ?? peerId;
        return AttachmentInformation(attachment, outgoing, peerName, stored, transfer?.FailureDetail, failure);
    }

    internal static InformationDocument AttachmentInformation(ChatAttachment attachment, bool? outgoing, string? peerName,
        StoredTransfer? stored, string? failureDetail = null, bool failure = false)
    {
        if (failure)
            return new(Strings.Get("失败原因"), Strings.Get(outgoing is true ? "文件发送失败" : outgoing is false ? "文件接收失败" : "文件传输失败"),
                attachment.FileName, InformationIcon("information-error"), 330,
                [Field("失败阶段", null), Field("失败原因", failureDetail ?? stored?.FailureDetail, "Failure"),
                 Field("错误代码", stored?.FailureCode), Field("发生时间", stored?.Status == "Failed" ? InformationTime(DateTimeOffset.FromUnixTimeMilliseconds(stored.UpdatedAt)) : null)], "Failure");

        var fields = new List<InformationField>();
        if (attachment.IsImage)
        {
            var dimensions = FileInteractionService.ReadImageDimensions(attachment);
            fields.Add(Field("图片尺寸", dimensions is { } size ? $"{size.Width} × {size.Height}" : null));
        }
        fields.Add(Field("传输方向", outgoing is { } direction ? Strings.Get(direction ? "发送" : "接收") : null));
        fields.Add(Field("传输状态", outgoing is { } value ? AttachmentSummaryConverter.DescribeState(attachment, value) : attachment.StateText,
            attachment.State == "Completed" && !attachment.IsMissing ? "Success" : attachment.HasFailure ? "Failure" : "Normal"));
        fields.Add(Field(outgoing is true ? "目标设备" : outgoing is false ? "来源设备" : "设备", peerName));
        fields.Add(Field("保存位置", attachment.LocalPath ?? Strings.Get("尚未保存")));
        fields.Add(Field("完成时间", attachment.State == "Completed" && stored?.Status == "Completed"
            ? InformationTime(DateTimeOffset.FromUnixTimeMilliseconds(stored.UpdatedAt)) : null));
        if (!attachment.IsImage)
            fields.Add(Field("文件校验", attachment.State == "Completed" && stored?.Sha256 is { Length: 32 } ? Strings.Get("传输时已校验") : null));
        var type = attachment.MimeType switch { "application/pdf" => Strings.Get("PDF 文档"), "image/png" => Strings.Get("PNG 图片"),
            "image/jpeg" => Strings.Get("JPEG 图片"), "text/plain" => Strings.Get("文本文档"), _ => attachment.MimeType };
        return new(Strings.Get(attachment.IsImage ? "图片详情" : "附件详情"), attachment.FileName,
            $"{type} · {attachment.SizeText}", InformationIcon(attachment.IsImage ? "information-image" : "information-file"),
            370, fields, attachment.IsImage ? "Image" : "Normal");
    }
}
