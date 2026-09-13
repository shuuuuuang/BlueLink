using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BlueLink.Protocol;

namespace BlueLink.Domain;

public enum PeerPlatform { Android, Windows, Unknown }

public sealed record NearbyDevice(
    string Id,
    string Name,
    string Address,
    PeerPlatform Platform = PeerPlatform.Unknown,
    short? Rssi = null,
    DateTimeOffset LastSeen = default,
    bool CanInitiate = false,
    string DiscoveryId = "")
{
    public ConnectionPhase? AttemptPhase { get; init; }
    public string? AttemptDetail { get; init; }
    public bool IsConnecting => AttemptPhase is ConnectionPhase.Connecting or ConnectionPhase.SecureHandshake or ConnectionPhase.TrustRequired;
    public bool HasConnectionFailure => AttemptPhase == ConnectionPhase.Disconnected;
    public string ConnectButtonText => IsConnecting ? Localization.Strings.Get("连接中") : HasConnectionFailure ? Localization.Strings.Get("重试") : Localization.Strings.Get("连接");
    public string HomeDetail => HasConnectionFailure ? Localization.Strings.Get("连接失败，请重试") : CompactPresenceDetail;
    public override string ToString() => $"{Name}\n{Address}";
    public string DisplayName => Name;
    public string PresenceDetail => $"{Platform} · {Address}" + (Rssi is null ? "" : $" · {Rssi} dBm");
    public string CompactPresenceDetail => $"{Platform}" + (Rssi is null ? "" : $" · {Rssi} dBm");
}

public enum ConnectionPhase { Offline, Connecting, SecureHandshake, TrustRequired, Connected, Disconnected }
public enum MessageStatus { LocalQueued, Sending, Sent, Delivered, Read, Received, Failed }
public enum TransferStatus { Offered, Queued, Transferring, Paused, Resuming, Verifying, Committing, Completed, Rejected, Failed, Canceled, RemotePaused }
public enum DeviceAvailability { Connected, Offline, Connectable }

public sealed record ConversationSummary(string PeerId, string PeerName, PeerPlatform Platform,
    DeviceAvailability Availability, Guid? SessionId, string TransportAddress,
    int UnreadCount, DateTimeOffset LastActivityAt, DateTimeOffset? LastConnectedAt = null) : INotifyPropertyChanged
{
    // WPF indexes selected items by hash. Record-generated hashing would include
    // mutable transfer state and PropertyChanged subscribers, invalidating that index.
    public override int GetHashCode() => System.StringComparer.Ordinal.GetHashCode(PeerId);

    public bool UsbReady { get; init; }
    private TransferItem? _activeTransfer;
    public TransferItem? ActiveTransfer
    {
        get => _activeTransfer;
        internal set
        {
            if (ReferenceEquals(_activeTransfer, value)) return;
            _activeTransfer = value;
            PropertyChanged?.Invoke(this, new(nameof(ActiveTransfer)));
            PropertyChanged?.Invoke(this, new(nameof(HasActiveTransfer)));
        }
    }
    public bool HasActiveTransfer => ActiveTransfer is not null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayName => PeerName;
    public string PlatformIconSource => "/BlueLink;component/Assets/Figma/" + (Platform switch
    {
        PeerPlatform.Android => "phone.png", PeerPlatform.Windows => "desktop.png", _ => "generic.png"
    });
    public string AvailabilityText => Availability switch
    {
        DeviceAvailability.Connected => Localization.Strings.Get("已连接 · 端到端加密"),
        DeviceAvailability.Offline => Localization.Strings.Format($"离线 · {LastActivityAt.ToLocalTime():MM-dd HH:mm}"),
        _ => Localization.Strings.Get("附近可连接")
    };
    public string StatusGlyph => Availability == DeviceAvailability.Connected ? "●" : Availability == DeviceAvailability.Offline ? "○" : "◉";
    public string PlatformText => Platform switch { PeerPlatform.Android => "Android", PeerPlatform.Windows => "Windows", _ => "BlueLink" };
    public string LastSeenText => Availability != DeviceAvailability.Connected
        ? (LastConnectedAt is { } connectedAt ? Localization.Strings.Format($"上次连接 {connectedAt.ToLocalTime():MM-dd HH:mm}") : Localization.Strings.Get("可查看本地历史记录"))
        : AvailabilityText;
    public bool IsConnected => Availability == DeviceAvailability.Connected;
    public bool IsOffline => Availability == DeviceAvailability.Offline;
    public bool IsConnectable => Availability == DeviceAvailability.Connectable;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
}
public enum ChatItemKind { Text, Image, File, System }

public sealed record ChatAttachment(Guid AttachmentId, Guid TransferId, string FileName,
    string MimeType, long Size, string? LocalPath = null, string State = "Offered", string? PreviewPath = null,
    long CompletedBytes = 0, double BytesPerSecond = 0)
{
    public override string ToString() => FileName;
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public string FileIcon => "FileTypeIcon-" + Files.FileTypeCatalog.Classify(FileName, MimeType);
    public bool IsAvailable => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    public bool IsMissing => !string.IsNullOrWhiteSpace(LocalPath) && !File.Exists(LocalPath);
    public string? DisplayPath => !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath)
        ? PreviewPath : LocalPath;
    public string StateText => IsMissing ? Localization.Strings.Get("文件已移动或删除") : State switch
    {
        "Completed" => Localization.Strings.Get("已完成"),
        "Transferring" => Localization.Strings.Get("传输中"),
        "Paused" => Localization.Strings.Get("已暂停"),
        "RemotePaused" => Localization.Strings.Get("对端已暂停，等待继续"),
        "Resuming" => Localization.Strings.Get("正在恢复"),
        "Canceled" => Localization.Strings.Get("已取消"),
        "Verifying" => Localization.Strings.Get("正在校验"),
        "Committing" => Localization.Strings.Get("正在保存"),
        "Failed" => Localization.Strings.Get("失败"),
        "Rejected" => Localization.Strings.Get("未接收"),
        _ => Localization.Strings.Get("等待传输")
    };
    public bool IsTransferActive => State is "Offered" or "Queued" or "Transferring" or "Paused" or "RemotePaused" or
        "Resuming" or "Verifying" or "Committing";
    public bool CanOpen => State == "Completed" && IsAvailable;
    public bool CanPause => State is "Offered" or "Queued" or "Transferring" or "Resuming";
    public bool CanResume => State == "Paused";
    public bool HasFailure => State is "Failed" or "Rejected" or "Canceled";
    public bool CanDelete => !IsTransferActive;
    public double Progress => Size == 0 ? 1 : Math.Clamp((double)CompletedBytes / Size, 0, 1);
    public double ProgressPercent => Progress * 100;
    public bool ShowProgress => State is "Transferring" or "Resuming" or "Verifying" or "Committing";
    public string ProgressText => $"{Format(CompletedBytes)} / {Format(Size)}" +
        (BytesPerSecond > 0 && ShowProgress ? $" · {Format((long)BytesPerSecond)}/s" : "");
    public string SizeText => Size switch
    {
        >= 1L << 30 => $"{Size / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{Size / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{Size / (double)(1L << 10):0.0} KiB",
        _ => $"{Size} B"
    };
    private static string Format(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.0} KiB",
        _ => $"{value} B"
    };
}

public sealed record ChatItem(Guid Id, string Text, bool Outgoing, DateTimeOffset CreatedAt, MessageStatus Status,
    ChatItemKind Kind = ChatItemKind.Text, IReadOnlyList<ChatAttachment>? Attachments = null)
{
    public override string ToString() => HasText ? Text : string.Join("、", Attachments?.Select(a => a.FileName) ?? []);
    public string Sender => Outgoing ? Localization.Strings.Get("我") : Localization.Strings.Get("对方");
    public string Time => CreatedAt.ToLocalTime().ToString("HH:mm");
    public string DateGroup => CreatedAt.ToLocalTime().ToString("yyyyMMdd");
    public string GroupTimeText
    {
        get
        {
            var value = CreatedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            var date = value.Date;
            var time = value.ToString("HH:mm");
            if (date == today) return Localization.Strings.Format($"今天 {time}");
            if (date == today.AddDays(-1)) return Localization.Strings.Format($"昨天 {time}");
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var startOfWeek = today.AddDays(-daysSinceMonday);
            if (date >= startOfWeek)
            {
                var weekday = date.DayOfWeek switch
                {
                    DayOfWeek.Monday => Localization.Strings.Get("周一"), DayOfWeek.Tuesday => Localization.Strings.Get("周二"),
                    DayOfWeek.Wednesday => Localization.Strings.Get("周三"), DayOfWeek.Thursday => Localization.Strings.Get("周四"),
                    DayOfWeek.Friday => Localization.Strings.Get("周五"), DayOfWeek.Saturday => Localization.Strings.Get("周六"), _ => Localization.Strings.Get("周日")
                };
                return $"{weekday} {time}";
            }
            return value.ToString(Localization.Strings.Get(date.Year == today.Year ? "M月d日 HH:mm" : "yyyy年M月d日 HH:mm"), Localization.Strings.Culture);
        }
    }
    public string StatusText => Status switch
    {
        MessageStatus.LocalQueued => Localization.Strings.Get("等待发送"),
        MessageStatus.Sending => Localization.Strings.Get("发送中"),
        MessageStatus.Sent => Localization.Strings.Get("已发送"),
        MessageStatus.Delivered => Localization.Strings.Get("已送达"),
        MessageStatus.Read => Localization.Strings.Get("已读"),
        MessageStatus.Received => Localization.Strings.Get("已接收"),
        MessageStatus.Failed => Localization.Strings.Get("发送失败"),
        _ => Status.ToString()
    };
    public string MetaText => $"{Time} · {StatusText}";
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public sealed class TransferItem : INotifyPropertyChanged
{
    internal TransferItem Snapshot()
    {
        var snapshot = (TransferItem)MemberwiseClone();
        snapshot.PropertyChanged = null;
        return snapshot;
    }
    public override string ToString() => Name;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string PeerName { get; set; } = Localization.Strings.Get("对端");
    public string RouteText => Outgoing ? Localization.Strings.Format($"本机 → {PeerName}") : Localization.Strings.Format($"{PeerName} → 本机");
    public string SizeText => Format(TotalBytes);
    public string TimeText => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string GroupText => IsActive ? Localization.Strings.Get("进行中") : IsCompleted ? Localization.Strings.Get("完成") : Localization.Strings.Get("失败");
    public int GroupOrder => IsActive ? 0 : IsCompleted ? 1 : 2;
    public string FileIcon => "FileTypeIcon-" + Files.FileTypeCatalog.Classify(Name, MimeType);
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required long TotalBytes { get; init; }
    public required bool Outgoing { get; init; }
    public Guid? MessageId { get; set; }
    public Guid? AttachmentId { get; set; }
    public string MimeType { get; set; } = "application/octet-stream";
    private string? _localPath;
    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (string.Equals(_localPath, value, StringComparison.Ordinal)) return;
            _localPath = value;
            Changed(); Changed(nameof(CanLocate)); Changed(nameof(CanOpen)); Changed(nameof(CanRetry));
        }
    }
    private string? _failureDetail;
    public string? FailureDetail { get => _failureDetail; set { _failureDetail = value; Changed(); } }
    public string? PeerId { get; set; }
    public AttachmentRole Role { get; set; } = AttachmentRole.File;
    private long _completedBytes;
    private TransferStatus _status;
    private long _speedSampleBytes;
    private DateTimeOffset _speedSampleAt = DateTimeOffset.UtcNow;
    private double _bytesPerSecond;
    public long CompletedBytes
    {
        get => _completedBytes;
        set
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _speedSampleAt).TotalSeconds;
            if (value > _speedSampleBytes && elapsed >= .15)
            {
                var instant = (value - _speedSampleBytes) / elapsed;
                _bytesPerSecond = _bytesPerSecond <= 0 ? instant : _bytesPerSecond * .65 + instant * .35;
                _speedSampleBytes = value;
                _speedSampleAt = now;
            }
            _completedBytes = value;
            Changed(); Changed(nameof(Progress)); Changed(nameof(Detail)); Changed(nameof(SpeedText));
            Changed(nameof(RemainingText)); Changed(nameof(DeviceStatusText));
        }
    }
    public TransferStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            Changed(); Changed(nameof(Detail)); Changed(nameof(IsActive)); Changed(nameof(IsCompleted));
            Changed(nameof(IsFailed)); Changed(nameof(CanLocate)); Changed(nameof(CanPause));
            Changed(nameof(CanResume)); Changed(nameof(CanCancel)); Changed(nameof(CanRetry));
            Changed(nameof(CanOpen)); Changed(nameof(CanDelete)); Changed(nameof(StatusText)); Changed(nameof(ShowPauseAction));
            Changed(nameof(GroupText)); Changed(nameof(GroupOrder)); Changed(nameof(DeviceStatusText));
        }
    }
    public double Progress => TotalBytes == 0 ? 1 : Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1);
    public string Direction => Outgoing ? Localization.Strings.Get("发送") : Localization.Strings.Get("接收");
    public string DeviceStatusText
    {
        get
        {
            var label = Status switch
            {
                TransferStatus.Paused => Outgoing ? "已暂停发送" : "已暂停接收",
                TransferStatus.RemotePaused => Outgoing ? "等待对端继续接收" : "等待对端继续发送",
                TransferStatus.Transferring or TransferStatus.Resuming => Outgoing ? "正在发送文件" : "正在接收文件",
                _ => StatusText
            };
            return $"{Localization.Strings.Get(label)} · {Progress:P0}";
        }
    }
    public double BytesPerSecond => _bytesPerSecond;
    public string SpeedText => _bytesPerSecond > 0 ? $"{Format((long)_bytesPerSecond)}/s" : Localization.Strings.Get("正在估算速度");
    public string RemainingText => _bytesPerSecond > 0 && TotalBytes > CompletedBytes
        ? Localization.Strings.Format($"约 {TimeSpan.FromSeconds((TotalBytes - CompletedBytes) / _bytesPerSecond):mm\\:ss}")
        : "";
    public string Detail => $"{Format(CompletedBytes)} / {Format(TotalBytes)} · {StatusText}" +
        (Status == TransferStatus.Transferring && _bytesPerSecond > 0 ? $" · {SpeedText} {RemainingText}" : "");
    public string StatusText => Status switch
    {
        TransferStatus.Offered => Outgoing ? Localization.Strings.Get("等待对端接受") : Localization.Strings.Get("等待接收"),
        TransferStatus.Queued => Localization.Strings.Get("排队等待"),
        TransferStatus.Transferring => Localization.Strings.Get("传输中"),
        TransferStatus.Paused => Localization.Strings.Get("已暂停"),
        TransferStatus.RemotePaused => Localization.Strings.Get("对端已暂停，等待继续"),
        TransferStatus.Resuming => Localization.Strings.Get("正在恢复"),
        TransferStatus.Verifying => Localization.Strings.Get("校验中"),
        TransferStatus.Committing => Localization.Strings.Get("正在落盘"),
        TransferStatus.Completed => Localization.Strings.Get("已完成"),
        TransferStatus.Rejected => Localization.Strings.Get("未接收"),
        TransferStatus.Failed => Localization.Strings.Get("失败"),
        TransferStatus.Canceled => Localization.Strings.Get("已取消"),
        _ => Status.ToString()
    };
    public bool IsActive => Status is TransferStatus.Offered or TransferStatus.Queued or TransferStatus.Transferring
        or TransferStatus.Paused or TransferStatus.RemotePaused or TransferStatus.Resuming or TransferStatus.Verifying or TransferStatus.Committing;
    public bool IsCompleted => Status == TransferStatus.Completed;
    public bool IsFailed => Status is TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Canceled;
    public bool CanLocate => IsCompleted && !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    public bool CanPause => Status is TransferStatus.Offered or TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Resuming;
    public string PauseActionText => Localization.Strings.Get(Outgoing ? "暂停传输" : "暂停接收");
    public bool ShowPauseAction => Status is TransferStatus.Transferring or TransferStatus.Resuming;
    public bool CanResume => Status == TransferStatus.Paused;
    public bool CanCancel => IsActive || (!Outgoing && Status == TransferStatus.Rejected);
    public bool CanRetry => Outgoing && IsFailed && !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    public bool CanOpen => CanLocate;
    public bool CanDelete => !IsActive;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLocalizedText() => Changed(string.Empty);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private static string Format(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.0} KiB",
        _ => $"{value} B"
    };
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
