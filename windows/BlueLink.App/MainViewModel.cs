using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using BlueLink.Bluetooth;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Storage;

namespace BlueLink;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RfcommBluetoothService _bluetooth;
    private readonly IdentityStore _identity;
    private readonly BlueLinkDatabase _database;
    private readonly SessionSupervisor _sessions;
    private readonly Dictionary<Guid, List<ChatItem>> _sessionMessages = [];
    private readonly Dictionary<Guid, Dictionary<Guid, TransferItem>> _sessionTransfers = [];
    private readonly Dictionary<string, StoredPeer> _storedPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoredConversation> _storedConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _reconnectAfter = new(StringComparer.OrdinalIgnoreCase);
    private int _disposeStarted;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reconnectLoop;
    private Guid? _activeSessionId;
    private string? _activePeerId;
    private bool _isDialing;
    private CancellationTokenSource? _dialCancellation;
    private NearbyDevice? _selectedDevice;
    private ConnectionPhase _phase;
    private string _status = "蓝牙就绪";
    private string _detail = "仅通过 Bluetooth 通信";
    private bool _isScanning;
    private string _scanFeedback = "点击重新扫描查找附近设备";
    private BlueLinkSettings _settings = BlueLinkSettings.Defaults("");

    public ObservableCollection<NearbyDevice> Devices { get; } = [];
    public ObservableCollection<NearbyDevice> NearbyNewDevices { get; } = [];
    public ObservableCollection<SessionSnapshot> Sessions { get; } = [];
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ObservableCollection<ConversationSummary> ConnectedConversations { get; } = [];
    public ObservableCollection<ConversationSummary> OfflineConversations { get; } = [];
    public ObservableCollection<ChatItem> Messages { get; } = [];
    public ICollectionView MessagesView { get; }
    public ObservableCollection<TransferItem> Transfers { get; } = [];
    public ObservableCollection<TransferItem> AllTransfers { get; } = [];
    public string IdentityFingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        _identity.Identity.PublicKey)).Chunk(4).Take(6).Select(value => new string(value)).Aggregate((left, right) => $"{left}:{right}");
    public string DefaultDownloadDirectory => _database.DefaultDownloadDirectory;
    public MainViewModel(string? dataRoot = null)
    {
        _identity = new IdentityStore(dataRoot);
        _bluetooth = new RfcommBluetoothService(_identity.Identity.PublicKey);
        _database = dataRoot is null
            ? new BlueLinkDatabase(installRoot: ResolveInstallRoot())
            : new BlueLinkDatabase(dataRoot, dataRoot);
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatItem.DateGroup)));
        _sessions = new SessionSupervisor(_identity, ConfirmTrustAsync);
        _sessions.SessionChanged += OnSessionChanged;
        _sessions.MessageReceived += OnSessionMessage;
        _sessions.EnvelopeReceived += OnSessionEnvelope;
        _sessions.TransferChanged += OnSessionTransfer;
        _sessions.ReceiptReceived += OnSessionReceipt;
    }

    private static string ResolveInstallRoot()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return baseDirectory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && baseDirectory.Parent is not null
            ? baseDirectory.Parent.FullName
            : baseDirectory.FullName;
    }
    public NearbyDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            Raise(nameof(CanConnectSelected));
            Raise(nameof(ConnectActionText));
        }
    }
    public ConnectionPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            Raise(nameof(IsConnected));
            Raise(nameof(IsOfflineConversation));
            Raise(nameof(IsConnecting));
            Raise(nameof(ActivePeerSubtitle));
            Raise(nameof(ComposerPlaceholder));
            Raise(nameof(CanStartConnection));
            Raise(nameof(CanConnectSelected));
            Raise(nameof(ConnectActionText));
        }
    }
    public string Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) Raise(nameof(ActivePeerTitle)); }
    }
    public string Detail
    {
        get => _detail;
        private set { if (Set(ref _detail, value)) Raise(nameof(ActivePeerSubtitle)); }
    }
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            Raise(nameof(ScanActionText));
            Raise(nameof(CanScan));
        }
    }
    public string ScanFeedback { get => _scanFeedback; private set => Set(ref _scanFeedback, value); }
    public string ScanActionText => IsScanning ? "扫描中…" : "重新扫描";
    public bool CanScan => !IsScanning;
    public BlueLinkSettings Settings { get => _settings; private set => Set(ref _settings, value); }
    public bool IsConnected => Phase == ConnectionPhase.Connected;
    public bool HasActiveConversation => !string.IsNullOrWhiteSpace(_activePeerId);
    public bool IsOfflineConversation => HasActiveConversation && !IsConnected;
    public bool IsConnecting => Phase is ConnectionPhase.Connecting or ConnectionPhase.SecureHandshake or ConnectionPhase.TrustRequired;
    public int ActiveSessionCount => Sessions.Count(value => value.Phase == ConnectionPhase.Connected);
    public string ActiveSessionCountText => $"{ActiveSessionCount} 台已连接";
    public string ActivePeerTitle => HasActiveConversation ? Status : "选择设备开始聊天";
    public string ActivePeerSubtitle => HasActiveConversation
        ? (IsConnected ? "端到端加密 · BTX/1.1" : IsConnecting ? Detail : "设备离线 · 可查看本地历史记录")
        : "消息和文件只通过 Bluetooth 传输";
    public string ComposerPlaceholder => IsConnected ? "输入消息" : "设备离线，暂时无法发送消息";
    public bool CanStartConnection => !_isDialing && _sessions.CanAccept;
    public bool CanCancelConnection => _isDialing;
    public bool CanConnectSelected => SelectedDevice?.CanInitiate == true && CanStartConnection;
    public string ConnectActionText => SelectedDevice is null ? "选择附近设备" :
        CanConnectSelected && SelectedDevice.Platform == PeerPlatform.Android ? "连接 Android 设备" :
        CanConnectSelected ? "连接所选设备" : "等待对端开放连接";

    internal void UseVisualFixture(ConversationSummary conversation)
    {
        _activePeerId = conversation.PeerId;
        _activeSessionId = conversation.SessionId;
        Status = conversation.PeerName;
        Detail = "端到端加密 · BTX/1.1";
        Phase = ConnectionPhase.Connected;
        Raise(nameof(HasActiveConversation));
        Raise(nameof(ActiveSessionCount));
        Raise(nameof(ActiveSessionCountText));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _database.InitializeAsync(_identity);
            Settings = await _database.LoadSettingsAsync();
            SessionLog.Enabled = Settings.DiagnosticsEnabled;
            await ApplyRetentionAsync(Settings.RetentionPeriod);
            foreach (var peer in await _database.LoadPeersAsync()) _storedPeers[peer.PeerId] = peer;
            foreach (var conversation in await _database.LoadConversationsAsync())
                _storedConversations[conversation.PeerId] = conversation;
            foreach (var transfer in await LoadStoredTransfersAsync(null)) AllTransfers.Add(transfer);
            RefreshConversations();
            _sessions.MaxConcurrentSessions = Settings.MaxConcurrentConnections;
            _sessions.ReceiveDirectory = Settings.DownloadDirectory;
            _sessions.MaxReceiveBytes = Settings.ReceiveSizeLimitEnabled ? Settings.ReceiveSizeLimitBytes : long.MaxValue;
            _sessions.AutoAcceptFiles = Settings.AutoDownloadFiles;
            _bluetooth.ConnectionAccepted += connection => StartSessionAsync(connection, connection.ListenerRole);
            _bluetooth.DevicesChanged += OnDevicesChanged;
            await _bluetooth.StartAsync();
            _reconnectLoop ??= RunReconnectLoopAsync(_lifetime.Token);
            Status = string.IsNullOrWhiteSpace(_bluetooth.StartupWarning) ? "蓝牙就绪" : "蓝牙部分可用";
            if (Settings.ScanOnStartup) await ScanAsync();
            else Detail = "启动扫描已关闭；点击“重新扫描”查找附近设备";
        }
        catch (Exception failure) { SetState(ConnectionPhase.Offline, "蓝牙不可用", failure.Message); }
    }

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanFeedback = "正在扫描附近运行蓝联的设备…";
        SetState(Phase, Status, "正在扫描附近运行蓝联的设备…");
        try
        {
            var devices = await _bluetooth.ScanAsync();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ApplyDevices(devices);
                RefreshConversations();
                var summary = devices.Count == 0
                    ? "附近没有发现运行蓝联的设备"
                    : $"发现 {devices.Count} 台附近 BlueLink 设备";
                Detail = string.IsNullOrWhiteSpace(_bluetooth.StartupWarning)
                    ? summary
                    : $"{summary}；{_bluetooth.StartupWarning}";
                ScanFeedback = summary;
            });
        }
        catch (Exception failure)
        {
            Detail = failure.Message;
            ScanFeedback = $"扫描失败：{failure.Message}";
        }
        finally { IsScanning = false; }
    }

    private void OnDevicesChanged(IReadOnlyList<NearbyDevice> devices)
    {
        var application = Application.Current;
        if (application is null) return;
        _ = application.Dispatcher.InvokeAsync(() =>
        {
            ApplyDevices(devices);
            RefreshConversations();
            _ = TryAutoConnectAsync(devices);
            if (Phase is ConnectionPhase.Connecting or ConnectionPhase.SecureHandshake or
                ConnectionPhase.TrustRequired or ConnectionPhase.Connected or ConnectionPhase.Disconnected) return;
            Detail = devices.Count == 0
                ? "正在扫描附近运行蓝联的设备…"
                : $"正在扫描 · 已发现 {devices.Count} 台附近 BlueLink 设备";
        });
    }

    private void ApplyDevices(IReadOnlyList<NearbyDevice> devices)
    {
        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in devices
                     .Where(device => !string.IsNullOrWhiteSpace(device.Address))
                     .GroupBy(device => string.IsNullOrWhiteSpace(device.DiscoveryId)
                             ? NormalizeAddress(device.Address)
                             : device.DiscoveryId,
                         StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(device => device.CanInitiate)
                         .ThenByDescending(device => device.Rssi ?? int.MinValue).First())
                     .OrderByDescending(device => device.Rssi ?? int.MinValue))
        {
            Devices.Add(device);
        }
        SelectedDevice = selectedId is null ? null : Devices.FirstOrDefault(device => device.Id == selectedId);
        RefreshNearbyNewDevices();
    }

    public async Task ConnectAsync()
    {
        if (SelectedDevice is null || !CanConnectSelected) return;
        await ConnectDeviceAsync(SelectedDevice, automatic: false);
    }

    private async Task ConnectDeviceAsync(NearbyDevice device, bool automatic)
    {
        if (!device.CanInitiate || _isDialing || !_sessions.CanAccept) return;
        _isDialing = true;
        _dialCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected)); Raise(nameof(CanCancelConnection));
        SetState(ConnectionPhase.Connecting, device.Name,
            device.Platform == PeerPlatform.Android
                ? "正在通过 GATT 请求 Android 建立 RFCOMM 回连"
                : "正在建立 RFCOMM 通道");
        try
        {
            var connection = await _bluetooth.ConnectAsync(device, _dialCancellation.Token);
            await StartSessionAsync(connection, connection.ListenerRole, select: !automatic,
                transportAddress: device.Address);
        }
        catch (OperationCanceledException)
        {
            if (automatic) RegisterReconnectFailure(device.Address);
            SetState(ConnectionPhase.Disconnected, device.Name, "已取消本次连接，可稍后重试");
        }
        catch (Exception failure)
        {
            if (automatic) RegisterReconnectFailure(device.Address);
            SetState(ConnectionPhase.Disconnected, "连接失败", failure.Message);
        }
        finally
        {
            _dialCancellation?.Dispose();
            _dialCancellation = null;
            _isDialing = false;
            Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected)); Raise(nameof(CanCancelConnection));
        }
    }

    public void CancelConnection() => _dialCancellation?.Cancel();

    public async Task SendAsync(string text)
    {
        if (_activeSessionId is not { } sessionId || string.IsNullOrWhiteSpace(text)) return;
        var item = new ChatItem(Guid.NewGuid(), text.Trim(), true, DateTimeOffset.Now, MessageStatus.Sending);
        if (!_sessionMessages.TryGetValue(sessionId, out var sessionItems))
            _sessionMessages[sessionId] = sessionItems = [];
        sessionItems.Add(item);
        Messages.Add(item);
        var snapshot = Sessions.FirstOrDefault(value => value.SessionId == sessionId);
        if (snapshot?.PeerId is { } peerId) await PersistMessageAsync(snapshot, item, unread: false);
        try
        {
            await _sessions.SendChatAsync(sessionId, item.Text, item.Id);
            var sent = item with { Status = MessageStatus.Sent };
            ReplaceSessionMessage(sessionId, sent);
            ReplaceMessage(sent);
            if (snapshot?.PeerId is not null) await PersistMessageAsync(snapshot, sent, unread: false);
        }
        catch
        {
            var stillConnected = Sessions.Any(value => value.SessionId == sessionId && value.Phase == ConnectionPhase.Connected);
            var failed = item with { Status = stillConnected ? MessageStatus.Failed : MessageStatus.LocalQueued };
            ReplaceSessionMessage(sessionId, failed);
            ReplaceMessage(failed);
            if (snapshot?.PeerId is not null) await PersistMessageAsync(snapshot, failed, unread: false);
        }
    }

    public Task SendFileAsync(string path) => _activeSessionId is { } id
        ? _sessions.SendFileAsync(id, path) : Task.CompletedTask;

    public Task PauseTransferAsync(TransferItem transfer) => ResolveSessionId(transfer) is { } id
        ? _sessions.PauseTransferAsync(id, transfer.Id) : Task.CompletedTask;

    public Task ResumeTransferAsync(TransferItem transfer) => ResolveSessionId(transfer) is { } id
        ? _sessions.ResumeTransferAsync(id, transfer.Id) : Task.CompletedTask;

    public Task CancelTransferAsync(TransferItem transfer) => ResolveSessionId(transfer) is { } id
        ? _sessions.CancelTransferAsync(id, transfer.Id) : Task.CompletedTask;

    public Task RetryTransferAsync(TransferItem transfer)
    {
        if (!transfer.CanRetry || string.IsNullOrWhiteSpace(transfer.LocalPath)) return Task.CompletedTask;
        var sessionId = ResolveSessionId(transfer);
        return sessionId is { } id ? _sessions.RetryFileAsync(id, transfer.LocalPath, transfer) : Task.CompletedTask;
    }

    private Guid? ResolveSessionId(TransferItem transfer)
    {
        if (!string.IsNullOrWhiteSpace(transfer.PeerId))
            return Sessions.FirstOrDefault(value => string.Equals(value.PeerId, transfer.PeerId,
                StringComparison.OrdinalIgnoreCase))?.SessionId;
        return _activeSessionId;
    }

    private async Task StartSessionAsync(RfcommConnection connection, bool listenerRole, bool select = false,
        string? transportAddress = null)
    {
        var sessionId = await _sessions.AddAsync(connection, listenerRole, transportAddress);
        if (sessionId is null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                Detail = $"已达到最大连接数（{_sessions.MaxConcurrentSessions}）");
            return;
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (select || _activeSessionId is null) SelectSession(sessionId.Value);
            SetState(ConnectionPhase.SecureHandshake, connection.PeerName, "正在验证设备身份");
        });
    }

    public void SelectSession(Guid sessionId)
    {
        _activeSessionId = sessionId;
        var selected = Sessions.FirstOrDefault(value => value.SessionId == sessionId);
        _activePeerId = selected?.PeerId;
        RaiseActiveConversationState();
        Messages.Clear();
        if (_sessionMessages.TryGetValue(sessionId, out var messages)) foreach (var item in messages) Messages.Add(item);
        Transfers.Clear();
        if (_sessionTransfers.TryGetValue(sessionId, out var transfers)) foreach (var item in transfers.Values.OrderByDescending(value => value.Id)) Transfers.Add(item);
        if (selected is not null)
        {
            SetState(selected.Phase, selected.PeerName, selected.Detail);
            if (selected.PeerId is not null)
            {
                _ = MarkConversationReadAsync(selected.PeerId);
                _ = LoadHistoryAsync(selected.PeerId, sessionId);
            }
        }
    }

    public async Task SelectConversationAsync(string peerId)
    {
        _activePeerId = peerId;
        RaiseActiveConversationState();
        await MarkConversationReadAsync(peerId);
        var session = Sessions.FirstOrDefault(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (session is not null) { SelectSession(session.SessionId); return; }
        _activeSessionId = null;
        Messages.Clear();
        foreach (var item in await LoadStoredMessagesAsync(peerId)) Messages.Add(item);
        Transfers.Clear();
        foreach (var item in await LoadStoredTransfersAsync(peerId)) Transfers.Add(item);
        var peer = _storedPeers.GetValueOrDefault(peerId);
        SetState(ConnectionPhase.Offline, peer?.DisplayName ?? "离线设备", "设备离线 · 可查看历史记录");
    }

    public async Task ConnectConversationAsync(ConversationSummary conversation)
    {
        if (conversation.IsConnected)
        {
            await SelectConversationAsync(conversation.PeerId);
            return;
        }
        var device = Devices.FirstOrDefault(value => value.CanInitiate &&
            (string.Equals(value.Address, conversation.TransportAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.Id, conversation.TransportAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.Id, conversation.PeerId, StringComparison.OrdinalIgnoreCase) ||
             MatchesPeer(value, conversation.PeerId)));
        if (device is null) return;
        SelectedDevice = device;
        await ConnectAsync();
    }

    public async Task DisconnectConversationAsync(ConversationSummary conversation)
    {
        var session = conversation.SessionId is { } sessionId
            ? Sessions.FirstOrDefault(value => value.SessionId == sessionId)
            : Sessions.FirstOrDefault(value => string.Equals(value.PeerId, conversation.PeerId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        await _sessions.DisconnectAsync(session.SessionId);
    }

    public async Task DeleteMessageAsync(ChatItem item)
    {
        await _database.DeleteMessageAsync(item.Id.ToString("N"));
        foreach (var values in _sessionMessages.Values) values.RemoveAll(value => value.Id == item.Id);
        Messages.Remove(item);
    }

    public Task DeleteAttachmentMessageAsync(ChatAttachment attachment)
    {
        var message = Messages.FirstOrDefault(value => value.Attachments?.Any(item =>
            item.AttachmentId == attachment.AttachmentId) == true);
        return message is null ? Task.CompletedTask : DeleteMessageAsync(message);
    }

    public async Task ClearConversationAsync(string peerId)
    {
        await _database.ClearConversationMessagesAsync(ConversationId(peerId));
        foreach (var session in Sessions.Where(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase)))
            if (_sessionMessages.TryGetValue(session.SessionId, out var values)) values.Clear();
        if (string.Equals(_activePeerId, peerId, StringComparison.OrdinalIgnoreCase)) Messages.Clear();
    }

    public async Task SetTransferPanelExpandedAsync(bool expanded)
    {
        Settings = Settings with { TransferPanelExpanded = expanded };
        await _database.SaveSettingsAsync(Settings);
        await ApplyRetentionAsync(Settings.RetentionPeriod);
    }

    public async Task ExportDiagnosticsAsync(string targetPath)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueLink", "Logs", "windows-session.log");
        var value = File.Exists(source) ? await File.ReadAllTextAsync(source) : "BlueLink 尚未生成诊断日志。";
        value = Regex.Replace(value, @"(?i)\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\b", "**:**:**:**:**:**");
        value = Regex.Replace(value, @"(?i)\b[0-9a-f]{32,}\b", match => match.Value[..8] + "…");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) value = value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(targetPath, value);
    }

    private Task ApplyRetentionAsync(string retention)
    {
        var age = retention switch { "30d" => TimeSpan.FromDays(30), "90d" => TimeSpan.FromDays(90),
            "1y" => TimeSpan.FromDays(365), _ => (TimeSpan?)null };
        return age is null ? Task.CompletedTask : _database.DeleteHistoryBeforeAsync(
            DateTimeOffset.UtcNow.Subtract(age.Value).ToUnixTimeMilliseconds());
    }

    public async Task SaveSettingsAsync(BlueLinkSettings value)
    {
        Settings = value with { MaxConcurrentConnections = Math.Clamp(value.MaxConcurrentConnections, 1, 8) };
        _sessions.MaxConcurrentSessions = Settings.MaxConcurrentConnections;
        _sessions.ReceiveDirectory = Settings.DownloadDirectory;
        _sessions.MaxReceiveBytes = Settings.ReceiveSizeLimitEnabled ? Settings.ReceiveSizeLimitBytes : long.MaxValue;
        _sessions.AutoAcceptFiles = Settings.AutoDownloadFiles;
        SessionLog.Enabled = Settings.DiagnosticsEnabled;
        await _database.SaveSettingsAsync(Settings);
    }

    public async Task ForgetPeerAsync(string peerId)
    {
        _identity.RemoveTrust(peerId);
        if (_storedPeers.TryGetValue(peerId, out var peer))
        {
            peer = peer with { TrustState = StoredTrustState.Unknown };
            await _database.UpsertPeerAsync(peer); _storedPeers[peerId] = peer;
        }
        RefreshConversations();
    }

    public async Task ClearChatHistoryAsync()
    {
        await _database.ClearMessagesAsync();
        foreach (var values in _sessionMessages.Values) values.Clear();
        Messages.Clear();
    }

    public async Task ClearTransferHistoryAsync()
    {
        await _database.ClearTransfersAsync();
        foreach (var values in _sessionTransfers.Values) values.Clear();
        Transfers.Clear(); AllTransfers.Clear();
    }

    public async Task DeleteTransferAsync(TransferItem transfer)
    {
        if (!transfer.CanDelete) return;
        await _database.DeleteTransferAsync(transfer.Id.ToString("N"));
        foreach (var values in _sessionTransfers.Values) values.Remove(transfer.Id);
        Transfers.Remove(transfer);
        AllTransfers.Remove(transfer);
    }

    public async Task ClearCompletedTransfersAsync()
    {
        await _database.ClearCompletedTransfersAsync();
        foreach (var values in _sessionTransfers.Values)
            foreach (var id in values.Where(pair => pair.Value.Status == TransferStatus.Completed)
                         .Select(pair => pair.Key).ToArray()) values.Remove(id);
        foreach (var transfer in Transfers.Where(value => value.Status == TransferStatus.Completed).ToArray())
            Transfers.Remove(transfer);
        foreach (var transfer in AllTransfers.Where(value => value.Status == TransferStatus.Completed).ToArray())
            AllTransfers.Remove(transfer);
    }

    private void OnSessionChanged(SessionSnapshot snapshot) => Application.Current.Dispatcher.Invoke(() =>
    {
        var existing = Sessions.ToList().FindIndex(value => value.SessionId == snapshot.SessionId);
        if (snapshot.Phase == ConnectionPhase.Disconnected)
        {
            if (existing >= 0) Sessions.RemoveAt(existing);
        }
        else if (existing >= 0) Sessions[existing] = snapshot;
        else Sessions.Add(snapshot);
        if (_activeSessionId is null && snapshot.Phase != ConnectionPhase.Disconnected) _activeSessionId = snapshot.SessionId;
        if (_activeSessionId == snapshot.SessionId)
        {
            SetState(snapshot.Phase, snapshot.PeerName, snapshot.Detail);
            if (snapshot.Phase == ConnectionPhase.Disconnected)
            {
                _activeSessionId = Sessions.FirstOrDefault()?.SessionId;
                _activePeerId = Sessions.FirstOrDefault()?.PeerId ?? _activePeerId;
                if (_activeSessionId is { } replacement) SelectSession(replacement);
            }
        }
        Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected));
        Raise(nameof(ActiveSessionCount)); Raise(nameof(ActiveSessionCountText));
        RefreshConversations();
        _ = PersistSessionStateAsync(snapshot);
        if (snapshot.Phase == ConnectionPhase.Disconnected && Devices.Count > 0)
            _ = TryAutoConnectAsync(Devices.ToArray());
    });

    private void OnSessionMessage(SessionSnapshot snapshot, ChatItem message) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (!_sessionMessages.TryGetValue(snapshot.SessionId, out var values)) _sessionMessages[snapshot.SessionId] = values = [];
        values.Add(message);
        if (_activeSessionId == snapshot.SessionId) Messages.Add(message);
        _ = PersistMessageAsync(snapshot, message, unread: !message.Outgoing && _activePeerId != snapshot.PeerId);
    });

    private void OnSessionEnvelope(SessionSnapshot snapshot, ChatEnvelope envelope, bool outgoing) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (envelope.Attachments.Count == 0) return;
            _ = PersistEnvelopeAsync(snapshot, envelope, outgoing,
                unread: !outgoing && _activePeerId != snapshot.PeerId);
            var kind = envelope.Kind == ChatPayloadKind.Image ? ChatItemKind.Image : ChatItemKind.File;
            var visibleDescriptors = envelope.Kind == ChatPayloadKind.Image
                ? envelope.Attachments.Where(value => value.Role == AttachmentRole.ImageOriginal)
                : envelope.Attachments.Where(value => value.Role == AttachmentRole.File);
            var attachments = visibleDescriptors.Select(value => new ChatAttachment(value.AttachmentId,
                value.TransferId, value.FileName, value.MimeType, value.Size)).ToArray();
            var item = new ChatItem(envelope.MessageId, envelope.Body, outgoing,
                DateTimeOffset.FromUnixTimeMilliseconds(envelope.CreatedAt),
                outgoing ? MessageStatus.Sent : MessageStatus.Received, kind, attachments);
            if (!_sessionMessages.TryGetValue(snapshot.SessionId, out var values))
                _sessionMessages[snapshot.SessionId] = values = [];
            values.RemoveAll(value => value.Id == item.Id);
            values.Add(item);
            if (_activeSessionId == snapshot.SessionId)
            {
                var index = Messages.ToList().FindIndex(value => value.Id == item.Id);
                if (index >= 0) Messages[index] = item; else Messages.Add(item);
            }
        });

    private void OnSessionTransfer(SessionSnapshot snapshot, TransferItem transfer) => Application.Current.Dispatcher.Invoke(() =>
    {
        transfer.PeerId = snapshot.PeerId;
        if (transfer.Role == AttachmentRole.ImagePreview)
        {
            UpdateAttachmentInChat(snapshot.SessionId, transfer);
            _ = PersistPreviewAsync(snapshot, transfer);
            return;
        }
        if (!_sessionTransfers.TryGetValue(snapshot.SessionId, out var values)) _sessionTransfers[snapshot.SessionId] = values = [];
        values[transfer.Id] = transfer;
        UpdateAttachmentInChat(snapshot.SessionId, transfer);
        if (_activeSessionId == snapshot.SessionId) UpdateTransfer(transfer);
        _ = PersistTransferAsync(snapshot, transfer);
    });

    private void UpdateAttachmentInChat(Guid sessionId, TransferItem transfer)
    {
        if (transfer.MessageId is not { } messageId || !_sessionMessages.TryGetValue(sessionId, out var messages)) return;
        var index = messages.FindIndex(value => value.Id == messageId);
        if (index < 0) return;
        var current = messages[index];
        var attachments = current.Attachments?.Select(value =>
            transfer.Role == AttachmentRole.ImagePreview && value.IsImage ? value with
            {
                PreviewPath = transfer.LocalPath ?? value.PreviewPath,
                State = value.State
            } : value.TransferId == transfer.Id ? value with
            {
                LocalPath = transfer.LocalPath ?? value.LocalPath,
                State = transfer.Status.ToString(),
                CompletedBytes = transfer.CompletedBytes,
                BytesPerSecond = transfer.BytesPerSecond
            } : value).ToArray();
        messages[index] = current with { Attachments = attachments };
        if (_activeSessionId == sessionId)
        {
            var visible = Messages.ToList().FindIndex(value => value.Id == messageId);
            if (visible >= 0) Messages[visible] = messages[index];
        }
    }

    private void OnSessionReceipt(SessionSnapshot snapshot, ChatReceipt receipt) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (!_sessionMessages.TryGetValue(snapshot.SessionId, out var values)) return;
        var index = values.FindIndex(value => value.Id == receipt.MessageId);
        if (index < 0) return;
        var status = receipt.State switch { ReceiptState.Read => MessageStatus.Read, ReceiptState.Delivered => MessageStatus.Delivered, _ => MessageStatus.Failed };
        values[index] = values[index] with { Status = status };
        if (_activeSessionId == snapshot.SessionId) ReplaceMessage(values[index]);
        _ = PersistMessageAsync(snapshot, values[index], unread: false);
    });

    private async Task<bool> ConfirmTrustAsync(string peerName, string safetyCode, string remoteFingerprint)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SetState(ConnectionPhase.TrustRequired, peerName, "请核对两端安全代码");
            return new TrustConfirmationWindow(peerName, safetyCode, IdentityFingerprint, remoteFingerprint)
            {
                Owner = Application.Current.MainWindow
            }.ShowDialog() == true;
        });
    }

    private void UpdateTransfer(TransferItem value)
    {
        var current = Transfers.FirstOrDefault(item => item.Id == value.Id);
        if (current is null)
        {
            Transfers.Insert(0, new TransferItem { Id = value.Id, Name = value.Name, TotalBytes = value.TotalBytes,
                Outgoing = value.Outgoing, CompletedBytes = value.CompletedBytes, Status = value.Status,
                MessageId = value.MessageId, AttachmentId = value.AttachmentId, MimeType = value.MimeType,
                LocalPath = value.LocalPath, FailureDetail = value.FailureDetail, PeerId = value.PeerId,
                Role = value.Role });
        }
        else
        {
            current.CompletedBytes = value.CompletedBytes; current.Status = value.Status;
            current.MessageId = value.MessageId ?? current.MessageId;
            current.AttachmentId = value.AttachmentId ?? current.AttachmentId;
            current.MimeType = value.MimeType;
            current.Role = value.Role;
            current.LocalPath = value.LocalPath ?? current.LocalPath;
            current.FailureDetail = value.FailureDetail;
            current.PeerId = value.PeerId ?? current.PeerId;
        }
    }

    private void ReplaceMessage(ChatItem value)
    {
        var index = Messages.ToList().FindIndex(item => item.Id == value.Id);
        if (index >= 0) Messages[index] = value;
    }

    private void ReplaceSessionMessage(Guid sessionId, ChatItem value)
    {
        if (!_sessionMessages.TryGetValue(sessionId, out var messages)) return;
        var index = messages.FindIndex(item => item.Id == value.Id);
        if (index >= 0) messages[index] = value;
    }

    private async Task PersistSessionStateAsync(SessionSnapshot snapshot)
    {
        if (snapshot.PeerId is not { } peerId) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (snapshot.Phase == ConnectionPhase.Connected)
        {
            var previous = _storedPeers.GetValueOrDefault(peerId);
            var key = _identity.TrustedIdentities.FirstOrDefault(value =>
                value.PeerIdHex.Equals(peerId, StringComparison.OrdinalIgnoreCase))?.PublicKey;
            var peer = new StoredPeer(peerId, snapshot.PeerName, "Android", StoredTrustState.Trusted,
                key ?? previous?.IdentityPublicKey, previous?.CreatedAt ?? now, now, now, snapshot.TransportAddress);
            await _database.UpsertPeerAsync(peer);
            _storedPeers[peerId] = peer;
            var conversation = _storedConversations.GetValueOrDefault(peerId) ??
                new StoredConversation(ConversationId(peerId), peerId, now, 0);
            await _database.UpsertConversationAsync(conversation);
            _storedConversations[peerId] = conversation;
            _reconnectAfter.Remove(snapshot.TransportAddress);
            await _database.UpsertSessionRecordAsync(new(snapshot.SessionId.ToString("N"), peerId,
                snapshot.StartedAt.ToUnixTimeMilliseconds(), "Connected", now, null, null));
            await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
            await LoadHistoryAsync(peerId, snapshot.SessionId);
            await FlushQueuedMessagesAsync(snapshot);
        }
        else if (snapshot.Phase == ConnectionPhase.Disconnected)
        {
            await _database.UpsertSessionRecordAsync(new(snapshot.SessionId.ToString("N"), peerId,
                snapshot.StartedAt.ToUnixTimeMilliseconds(), "Disconnected", snapshot.StartedAt.ToUnixTimeMilliseconds(),
                now, snapshot.Detail));
        }
    }

    private async Task EnsureConversationAsync(SessionSnapshot snapshot, long activityAt)
    {
        if (snapshot.PeerId is not { } peerId) return;
        if (!_storedPeers.TryGetValue(peerId, out var peer))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            peer = new StoredPeer(peerId, snapshot.PeerName, "Android", StoredTrustState.Trusted, null,
                now, now, now, snapshot.TransportAddress);
            await _database.UpsertPeerAsync(peer);
            _storedPeers[peerId] = peer;
        }
        if (!_storedConversations.ContainsKey(peerId))
        {
            var conversation = new StoredConversation(ConversationId(peerId), peerId, activityAt, 0);
            await _database.UpsertConversationAsync(conversation);
            _storedConversations[peerId] = conversation;
        }
    }

    private async Task PersistMessageAsync(SessionSnapshot snapshot, ChatItem item, bool unread)
    {
        if (!Settings.SaveChatHistory || snapshot.PeerId is not { } peerId) return;
        var createdAt = item.CreatedAt.ToUnixTimeMilliseconds();
        await EnsureConversationAsync(snapshot, createdAt);
        var stored = new StoredMessage(item.Id.ToString("N"), ConversationId(peerId), peerId,
            item.Outgoing ? StoredMessageDirection.Outgoing : StoredMessageDirection.Incoming,
            item.Kind switch { ChatItemKind.Image => StoredMessageType.Image, ChatItemKind.File => StoredMessageType.File,
                ChatItemKind.System => StoredMessageType.System, _ => StoredMessageType.Text },
            item.Text, item.Status.ToString(), createdAt, createdAt);
        await _database.UpsertMessageAsync(stored);
        var current = _storedConversations[peerId];
        current = current with { LastActivityAt = Math.Max(current.LastActivityAt, createdAt),
            UnreadCount = current.UnreadCount + (unread ? 1 : 0) };
        await _database.UpsertConversationAsync(current);
        _storedConversations[peerId] = current;
        await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
    }

    private async Task PersistEnvelopeAsync(SessionSnapshot snapshot, ChatEnvelope envelope, bool outgoing, bool unread)
    {
        if (!Settings.SaveChatHistory || snapshot.PeerId is not { } peerId) return;
        var kind = envelope.Kind switch { ChatPayloadKind.Image => ChatItemKind.Image,
            ChatPayloadKind.File => ChatItemKind.File, ChatPayloadKind.System => ChatItemKind.System, _ => ChatItemKind.Text };
        var item = new ChatItem(envelope.MessageId, envelope.Body, outgoing,
            DateTimeOffset.FromUnixTimeMilliseconds(envelope.CreatedAt),
            outgoing ? MessageStatus.Sent : MessageStatus.Received, kind);
        await PersistMessageAsync(snapshot, item, unread);
        var persistedAttachments = envelope.Kind == ChatPayloadKind.Image
            ? envelope.Attachments.Where(value => value.Role == AttachmentRole.ImageOriginal)
            : envelope.Attachments.Where(value => value.Role == AttachmentRole.File);
        foreach (var attachment in persistedAttachments)
            await _database.UpsertAttachmentAsync(new(attachment.AttachmentId.ToString("N"),
                envelope.MessageId.ToString("N"), attachment.TransferId.ToString("N"), attachment.FileName,
                attachment.MimeType, attachment.Size, attachment.Sha256, null, null, "Offered"));
    }

    private async Task PersistTransferAsync(SessionSnapshot snapshot, TransferItem transfer)
    {
        if (!Settings.SaveTransferHistory || snapshot.PeerId is not { } peerId) return;
        transfer.PeerId = peerId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await EnsureConversationAsync(snapshot, now);
        var previous = (await _database.LoadTransfersAsync(peerId)).FirstOrDefault(value =>
            value.TransferId.Equals(transfer.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
        await _database.UpsertTransferAsync(new(transfer.Id.ToString("N"), peerId,
            transfer.MessageId?.ToString("N") ?? previous?.MessageId,
            transfer.Outgoing ? "Outgoing" : "Incoming", transfer.Status.ToString(), transfer.Name,
            transfer.MimeType, transfer.TotalBytes, transfer.CompletedBytes, transfer.LocalPath ?? previous?.LocalPath,
            previous?.SnapshotPath, previous?.Sha256, transfer.Status == TransferStatus.Failed ? "TRANSFER_FAILED" : null,
            transfer.FailureDetail, previous?.CreatedAt ?? now, now));
        if (transfer.AttachmentId is { } attachmentId)
        {
            var storedAttachments = await _database.LoadAttachmentsAsync(transfer.MessageId?.ToString("N") ?? "");
            var attachment = transfer.Role == AttachmentRole.ImagePreview
                ? storedAttachments.FirstOrDefault(value => value.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                : storedAttachments.FirstOrDefault(value => value.AttachmentId.Equals(attachmentId.ToString("N"), StringComparison.OrdinalIgnoreCase));
            if (attachment is not null) await _database.UpsertAttachmentAsync(attachment with
            {
                TransferId = transfer.Role == AttachmentRole.ImagePreview ? attachment.TransferId : transfer.Id.ToString("N"),
                LocalPath = transfer.Role == AttachmentRole.ImagePreview ? attachment.LocalPath : transfer.LocalPath ?? attachment.LocalPath,
                PreviewPath = transfer.Role == AttachmentRole.ImagePreview ? transfer.LocalPath ?? attachment.PreviewPath : attachment.PreviewPath,
                State = transfer.Role == AttachmentRole.ImagePreview && transfer.Status == TransferStatus.Failed
                    ? attachment.State : transfer.Status.ToString()
            });
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var existing = AllTransfers.FirstOrDefault(value => value.Id == transfer.Id);
            if (existing is null) AllTransfers.Insert(0, transfer);
            else
            {
                existing.CompletedBytes = transfer.CompletedBytes; existing.Status = transfer.Status;
                existing.LocalPath = transfer.LocalPath ?? existing.LocalPath;
                existing.FailureDetail = transfer.FailureDetail; existing.PeerId = peerId;
            }
        });
    }

    private async Task PersistPreviewAsync(SessionSnapshot snapshot, TransferItem transfer)
    {
        if (!Settings.SaveChatHistory || transfer.MessageId is null || string.IsNullOrWhiteSpace(transfer.LocalPath)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var attachments = await _database.LoadAttachmentsAsync(transfer.MessageId.Value.ToString("N"));
            var image = attachments.FirstOrDefault(value =>
                value.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
            if (image is not null)
            {
                await _database.UpsertAttachmentAsync(image with { PreviewPath = transfer.LocalPath });
                return;
            }
            await Task.Delay(50);
        }
    }

    private async Task LoadHistoryAsync(string peerId, Guid sessionId)
    {
        var persisted = await LoadStoredMessagesAsync(peerId);
        var transient = _sessionMessages.GetValueOrDefault(sessionId) ?? [];
        var merged = persisted.Concat(transient).GroupBy(value => value.Id).Select(group => group.Last())
            .OrderBy(value => value.CreatedAt).ToList();
        _sessionMessages[sessionId] = merged;
        var transfers = await LoadStoredTransfersAsync(peerId);
        _sessionTransfers[sessionId] = transfers.ToDictionary(value => value.Id);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_activeSessionId != sessionId) return;
            Messages.Clear(); foreach (var item in merged) Messages.Add(item);
            Transfers.Clear(); foreach (var item in transfers) Transfers.Add(item);
        });
    }

    private async Task FlushQueuedMessagesAsync(SessionSnapshot snapshot)
    {
        if (snapshot.PeerId is null || snapshot.Phase != ConnectionPhase.Connected) return;
        if (!_sessionMessages.TryGetValue(snapshot.SessionId, out var messages)) return;
        foreach (var queued in messages.Where(value => value.Outgoing && value.Status == MessageStatus.LocalQueued).ToArray())
        {
            try
            {
                await _sessions.SendChatAsync(snapshot.SessionId, queued.Text, queued.Id);
                var sent = queued with { Status = MessageStatus.Sent };
                ReplaceSessionMessage(snapshot.SessionId, sent);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_activeSessionId == snapshot.SessionId) ReplaceMessage(sent);
                });
                await PersistMessageAsync(snapshot, sent, unread: false);
            }
            catch
            {
                // Keep LOCAL_QUEUED; the next successfully restored session will retry it.
                break;
            }
        }
    }

    private async Task<List<ChatItem>> LoadStoredMessagesAsync(string peerId)
    {
        var result = new List<ChatItem>();
        foreach (var value in await _database.LoadMessagesAsync(ConversationId(peerId)))
        {
            var attachments = (await _database.LoadAttachmentsAsync(value.MessageId)).Select(item =>
                new ChatAttachment(Guid.ParseExact(item.AttachmentId, "N"),
                    item.TransferId is null ? Guid.Empty : Guid.ParseExact(item.TransferId, "N"),
                    item.FileName, item.MimeType, item.Size, item.LocalPath, item.State, item.PreviewPath)).ToArray();
            _ = Enum.TryParse<MessageStatus>(value.Status, true, out var status);
            result.Add(new(Guid.ParseExact(value.MessageId, "N"), value.Content,
                value.Direction == StoredMessageDirection.Outgoing, DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAt),
                status, value.Type switch { StoredMessageType.Image => ChatItemKind.Image,
                    StoredMessageType.File => ChatItemKind.File, StoredMessageType.System => ChatItemKind.System,
                    _ => ChatItemKind.Text }, attachments));
        }
        return result;
    }

    private async Task<List<TransferItem>> LoadStoredTransfersAsync(string? peerId) =>
        (await _database.LoadTransfersAsync(peerId)).Select(value => new TransferItem
        {
            Id = Guid.ParseExact(value.TransferId, "N"), Name = value.FileName, TotalBytes = value.TotalBytes,
            Outgoing = value.Direction.Equals("Outgoing", StringComparison.OrdinalIgnoreCase),
            CompletedBytes = value.CompletedBytes,
            Status = Enum.TryParse<TransferStatus>(value.Status, true, out var status) ? status : TransferStatus.Failed,
            MessageId = value.MessageId is null ? null : Guid.ParseExact(value.MessageId, "N"),
            MimeType = value.MimeType, LocalPath = value.LocalPath, FailureDetail = value.FailureDetail,
            PeerId = value.PeerId
        }).ToList();

    private void RefreshConversations()
    {
        var active = Sessions.Where(value => value.PeerId is not null)
            .ToDictionary(value => value.PeerId!, StringComparer.OrdinalIgnoreCase);
        var canonicalPeers = _storedPeers.Values
            .GroupBy(PeerProjectionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(peer => active.ContainsKey(peer.PeerId))
                .ThenByDescending(peer => peer.LastConnectedAt ?? peer.LastSeenAt).First());
        var values = canonicalPeers.Select(peer =>
        {
            active.TryGetValue(peer.PeerId, out var session);
            var availability = session is not null ? DeviceAvailability.Connected :
                Devices.Any(device => device.Address.Equals(peer.TransportAddress, StringComparison.OrdinalIgnoreCase) ||
                    MatchesPeer(device, peer.PeerId)) ? DeviceAvailability.Connectable : DeviceAvailability.Offline;
            var conversation = _storedConversations.GetValueOrDefault(peer.PeerId);
            return new ConversationSummary(peer.PeerId, string.IsNullOrWhiteSpace(peer.DisplayName) ? "已信任设备" : peer.DisplayName,
                Enum.TryParse<PeerPlatform>(peer.Platform, true, out var platform) ? platform : PeerPlatform.Unknown,
                availability, session?.SessionId, peer.TransportAddress, conversation?.UnreadCount ?? 0,
                DateTimeOffset.FromUnixTimeMilliseconds(conversation?.LastActivityAt ?? peer.LastSeenAt));
        }).OrderBy(value => value.Availability switch
        {
            DeviceAvailability.Connected => 0, DeviceAvailability.Offline => 1, _ => 2
        }).ThenByDescending(value => value.LastActivityAt).ToList();
        Conversations.Clear(); foreach (var value in values) Conversations.Add(value);
        ConnectedConversations.Clear();
        OfflineConversations.Clear();
        foreach (var value in values)
        {
            switch (value.Availability)
            {
                case DeviceAvailability.Connected: ConnectedConversations.Add(value); break;
                case DeviceAvailability.Offline:
                case DeviceAvailability.Connectable:
                    OfflineConversations.Add(value);
                    break;
            }
        }
        RefreshNearbyNewDevices();
        Raise(nameof(ActiveSessionCount)); Raise(nameof(ActiveSessionCountText));
    }

    private void RefreshNearbyNewDevices()
    {
        var knownAddresses = _storedPeers.Values
            .Select(peer => NormalizeAddress(peer.TransportAddress))
            .Where(address => address.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var address in Sessions.Select(session => NormalizeAddress(session.TransportAddress)))
            knownAddresses.Add(address);
        var knownPeerIds = _storedPeers.Keys.Concat(Sessions.Where(session => session.PeerId is not null)
            .Select(session => session.PeerId!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectedAddress = SelectedDevice is null ? null : NormalizeAddress(SelectedDevice.Address);
        NearbyNewDevices.Clear();
        foreach (var device in Devices.Where(device =>
                     !knownAddresses.Contains(NormalizeAddress(device.Address)) &&
                     !knownPeerIds.Any(peerId => MatchesPeer(device, peerId))))
            NearbyNewDevices.Add(device);
        SelectedDevice = selectedAddress is null
            ? null
            : NearbyNewDevices.FirstOrDefault(device => NormalizeAddress(device.Address) == selectedAddress);
    }

    private static string PeerProjectionKey(StoredPeer peer)
    {
        return $"peer:{peer.PeerId}";
    }

    private static bool MatchesPeer(NearbyDevice device, string peerId) =>
        !string.IsNullOrWhiteSpace(device.DiscoveryId) &&
        peerId.StartsWith(device.DiscoveryId, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAddress(string? address) =>
        string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim().ToUpperInvariant();

    private async Task TryAutoConnectAsync(IReadOnlyList<NearbyDevice> devices)
    {
        if (!Settings.AutoConnectTrustedDevices || _isDialing || !_sessions.CanAccept) return;
        var now = DateTimeOffset.UtcNow;
        var connectedPeers = Sessions.Where(value => value.PeerId is not null)
            .Select(value => value.PeerId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeAddresses = Sessions.Select(value => value.TransportAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _storedPeers.Values.Where(peer => peer.TrustState == StoredTrustState.Trusted &&
                !connectedPeers.Contains(peer.PeerId) && !string.IsNullOrWhiteSpace(peer.TransportAddress))
            .Select(peer => devices.FirstOrDefault(device => device.CanInitiate &&
                (device.Address.Equals(peer.TransportAddress, StringComparison.OrdinalIgnoreCase) ||
                 MatchesPeer(device, peer.PeerId))))
            .Where(device => device is not null && !activeAddresses.Contains(device.Address) &&
                (!_reconnectAfter.TryGetValue(device.Address, out var retry) || retry <= now))
            .Cast<NearbyDevice>()
            .DistinctBy(device => string.IsNullOrWhiteSpace(device.DiscoveryId)
                    ? device.Address : device.DiscoveryId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (!_sessions.CanAccept) break;
            _reconnectAfter[candidate.Address] = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            activeAddresses.Add(candidate.Address);
            await ConnectDeviceAsync(candidate, automatic: true);
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Volatile.Read(ref _disposeStarted) != 0) break;
                if (!Settings.AutoConnectTrustedDevices || Devices.Count == 0) continue;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) continue;
                await dispatcher.InvokeAsync(() => TryAutoConnectAsync(Devices.ToArray())).Task.Unwrap();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task MarkConversationReadAsync(string peerId)
    {
        if (!_storedConversations.TryGetValue(peerId, out var conversation) || conversation.UnreadCount == 0) return;
        conversation = conversation with { UnreadCount = 0 };
        await _database.UpsertConversationAsync(conversation);
        _storedConversations[peerId] = conversation;
        await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
    }

    private void RegisterReconnectFailure(string address)
    {
        var previous = _reconnectAfter.TryGetValue(address, out var retry) ? retry : DateTimeOffset.UtcNow;
        var delay = previous > DateTimeOffset.UtcNow ? TimeSpan.FromSeconds(Math.Min(300,
            Math.Max(10, (previous - DateTimeOffset.UtcNow).TotalSeconds * 2))) : TimeSpan.FromSeconds(10);
        _reconnectAfter[address] = DateTimeOffset.UtcNow + delay;
    }

    private static string ConversationId(string peerId) => $"peer:{peerId.ToLowerInvariant()}";

    private void RaiseActiveConversationState()
    {
        Raise(nameof(HasActiveConversation));
        Raise(nameof(IsOfflineConversation));
        Raise(nameof(ActivePeerTitle));
        Raise(nameof(ActivePeerSubtitle));
        Raise(nameof(ComposerPlaceholder));
    }

    private void SetState(ConnectionPhase phase, string status, string detail)
    {
        Phase = phase; Status = status; Detail = detail;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _lifetime.Cancel();
        if (_reconnectLoop is not null)
            try { await _reconnectLoop; } catch (OperationCanceledException) { }
        _bluetooth.DevicesChanged -= OnDevicesChanged;
        await _sessions.DisposeAsync();
        _bluetooth.Dispose();
        _lifetime.Dispose();
    }
}
