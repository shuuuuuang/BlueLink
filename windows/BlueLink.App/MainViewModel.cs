using System.Collections.ObjectModel;
using System.ComponentModel;
using BlueLink.Feedback;
using System.Windows;
using System.Windows.Data;
using BlueLink.Bluetooth;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Storage;
using BlueLink.Transport;
using BlueLink.Usb;

namespace BlueLink;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private RfcommBluetoothService _bluetooth;
    private bool _runtimeStarted;
    public Notifications.NotificationCenter Notifications { get; } = new();
    private bool _windowHasFocus;
    public bool IsSettingsOpen { get; internal set; }
    public bool IsConversationVisible(string? peerId) => _windowHasFocus && !IsSettingsOpen && !ShowFiles && _activePeerId == peerId;
    public async Task SetWindowFocusAsync(bool focused)
    {
        _windowHasFocus = focused;
        if (focused && !IsSettingsOpen && !ShowFiles && _activePeerId is { } id) await MarkConversationReadAsync(id);
    }

    private volatile bool _resettingIdentity;
    private readonly SemaphoreSlim _trustMutationGate = new(1, 1);
    private readonly SemaphoreSlim _conversationPersistenceGate = new(1, 1);
    private readonly SemaphoreSlim _transferPersistenceGate = new(1, 1);
    private readonly IdentityStore _identity;
    private readonly BlueLinkDatabase _database;
    private readonly SessionSupervisor _sessions;
    private readonly TransferRecoveryStore _recovery;
    private readonly HashSet<Guid> _historyLoaded = [];
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private long _conversationSelectionVersion;
    public bool ActiveUsbReady => ShowPeerIdentity && UsbSessionPolicy.IsReady(Settings.UsbEnabled, _activePeerId, Sessions);
    public string? ActiveUsbNotice => ShowPeerIdentity ? UsbSessionPolicy.Notice(Settings.UsbEnabled, _activePeerId, Sessions, Array.Empty<UsbSnapshot>()) is { } text
        ? Localization.Strings.Get(text) : null : null;
    public bool HasActiveUsbNotice => ActiveUsbNotice is not null;
    private void UsbChanged(UsbSnapshot _)
    {
        if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher) return;
        dispatcher.BeginInvoke(new Action(() => { if (Volatile.Read(ref _disposeStarted) != 0) return; RaiseUsbNotice(); }));
    }
    private void RaiseUsbNotice() { Raise(nameof(ActiveUsbNotice)); Raise(nameof(HasActiveUsbNotice)); }

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
    private string _status = Localization.Strings.Get("蓝牙就绪");
    private string _detail = Localization.Strings.Get("通过 Bluetooth 或 USB 安全通信");
    private bool _isScanning;
    private string _scanFeedback = "";
    private BlueLinkSettings _settings = BlueLinkSettings.Defaults("");

    public ObservableCollection<NearbyDevice> Devices { get; } = [];
    public ObservableCollection<NearbyDevice> NearbyNewDevices { get; } = [];
    public ObservableCollection<SessionSnapshot> Sessions { get; } = [];
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ObservableCollection<ConversationSummary> TrustedDevices { get; } = [];
    public bool HasTrustedDevices => TrustedDevices.Count > 0;
    public string TrustedDeviceCountText => Localization.Strings.Format($"已信任 {TrustedDevices.Count} 台设备");
    public ObservableCollection<ConversationSummary> ConnectedConversations { get; } = [];
    public ObservableCollection<ConversationSummary> OfflineConversations { get; } = [];
    public ObservableCollection<ChatItem> Messages { get; } = [];
    public ICollectionView MessagesView { get; }
    public ObservableCollection<TransferItem> Transfers { get; } = [];
    public ObservableCollection<TransferItem> AllTransfers { get; } = [];
    public string IdentityFingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        _identity.Identity.PublicKey)).Chunk(4).Take(6).Select(value => new string(value)).Aggregate((left, right) => $"{left}:{right}");
    public string DefaultDownloadDirectory => _database.DefaultDownloadDirectory;
    public string DataDirectory => Path.GetDirectoryName(_database.DatabasePath)!;
    public string CacheDirectory { get; }
    internal TemporaryCleanup CollectTemporaryFiles(bool preview) => OwnedTemporaryFiles.Collect(
        Path.Combine(CacheDirectory, "Outgoing"), _recovery.ReferencesTemporary, preview);
    internal StorageInventory MeasureStorage(string received, CancellationToken token)
    {
        var locations = new List<StorageLocation> { new(DataDirectory,StorageCategory.Other), new(received,StorageCategory.Received) };
        foreach (var location in new[] { new StorageLocation(CacheDirectory,StorageCategory.Other),
            new(Path.Combine(CacheDirectory,"Thumbnails"),StorageCategory.Thumbnails),
            new(Path.Combine(CacheDirectory,"Updates"),StorageCategory.Updates),
            new(Path.Combine(CacheDirectory,"Outgoing"),StorageCategory.Snapshots),
            new(Path.Combine(DataDirectory,"composer"),StorageCategory.Drafts) })
            if (Directory.Exists(location.Path)) locations.Add(location);
        return StorageInventory.Scan(locations,token);
    }
    public string DiagnosticsPath => SessionLog.FilePath;
    public Updates.UpdateWorkflow Updates { get; private set; }
    public bool CanInstallUpdate => !_acceptanceUpdates && !AllTransfers.Any(value => value.IsActive);
    public string LocalDeviceDisplayName => LocalDeviceName.Resolve(Settings.LocalDeviceName);
    public MainViewModel(string? dataRoot = null)
    {
        CacheDirectory = Path.Combine(dataRoot ?? BlueLink.Storage.AppStoragePaths.UserDirectory, "Cache");
        Files.FileInteractionService.ThumbnailDirectory = Path.Combine(CacheDirectory, "Thumbnails");
        _identity = new IdentityStore(dataRoot);
        Updates = new(new Updates.UpdateService(Path.Combine(CacheDirectory, "Updates")));
        _bluetooth = new RfcommBluetoothService(_identity.Identity.PublicKey);
        _database = dataRoot is null
            ? new BlueLinkDatabase(installRoot: ResolveInstallRoot())
            : new BlueLinkDatabase(dataRoot, dataRoot);
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatItem.DateGroup)));
        InitializeHomeViews();
        _recovery = new TransferRecoveryStore(Path.Combine(DataDirectory, "Recovery"));
        _sessions = new SessionSupervisor(_identity, PresentTrustRequest);
        _sessions.PersistRecovery = (session, item) => _recovery.RecordTransfer(session.SessionId, session.StartedAt, session.Transport.ToString(), item);
        _sessions.IdentityAssociations = CreateIdentityAssociationHandler;
        _sessions.OutgoingDirectory = Path.Combine(CacheDirectory, "Outgoing");
        _sessions.ReceiveDecision = IncomingFilePrompt.DecideAsync;
        _sessions.SessionChanged += OnSessionChanged;
        _sessions.MessageReceived += OnSessionMessage;
        _sessions.EnvelopeReceived += OnSessionEnvelope;
        _sessions.TransferChanged += OnSessionTransfer;
        _sessions.ReceiptReceived += OnSessionReceipt;
    }

    private static string ResolveInstallRoot() => AppStoragePaths.ProgramDirectory;
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
            Raise(nameof(ComposerHint));
            Raise(nameof(ShowOfflineHistoryNotice));
            Raise(nameof(ActiveUsbReady)); RaiseUsbNotice();
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
            Raise(nameof(NearbyHeading));
            Raise(nameof(CanScan));
        }
    }
    public string ScanFeedback { get => _scanFeedback; private set => Set(ref _scanFeedback, value); }
    public string RefreshNearbyText => Localization.Strings.Get("扫描附近设备");
    // Used only by isolated desktop acceptance/verification; normal startup always uses Bluetooth.
    internal Func<Task<IReadOnlyList<NearbyDevice>>>? ScanForAcceptance { get; set; }
    public bool CanScan => !_resettingIdentity && !IsScanning && !IsBluetoothUnavailable;
    public BlueLinkSettings Settings { get => _settings; private set => Set(ref _settings, value); }
    public bool IsConnected => Phase == ConnectionPhase.Connected;
    public bool HasActiveConversation => !string.IsNullOrWhiteSpace(_activePeerId);
    public bool IsOfflineConversation => HasActiveConversation && !IsConnected;
    public bool IsConnecting => Phase is ConnectionPhase.Connecting or ConnectionPhase.SecureHandshake or ConnectionPhase.TrustRequired;
    public int ActiveSessionCount => Sessions.Count(value => value.Phase == ConnectionPhase.Connected);
    public string ActiveSessionCountText => Localization.Strings.Format($"{ActiveSessionCount} 台设备已连接");
    public string ActivePeerTitle => HasActiveConversation ? (Conversations.FirstOrDefault(p => p.PeerId.Equals(_activePeerId,StringComparison.OrdinalIgnoreCase))?.DisplayName ?? Status) : Localization.Strings.Get("选择设备开始聊天");
    public string ActivePeerSubtitle => HasActiveConversation
        ? (IsConnected ? Localization.Strings.Get(Sessions.FirstOrDefault(value => value.SessionId == _activeSessionId)?.Transport == TransportKind.Usb ? "USB · 端到端加密" : "Bluetooth · 端到端加密") : IsConnecting ? Detail : OfflinePeerSubtitle)
        : Localization.Strings.Get("消息和文件通过 Bluetooth 或 USB 安全传输");
    private string OfflinePeerSubtitle => string.Join(" · ",
        IsBluetoothUnavailable ? Localization.Strings.Get("蓝牙未开启") : Localization.Strings.Get("设备离线"),
        Conversations.FirstOrDefault(peer => peer.PeerId == _activePeerId)?.LastSeenText ?? Localization.Strings.Get("可查看本地历史记录"));
    public string ComposerPlaceholder => IsConnected ? Localization.Strings.Get("输入消息")
        : Localization.Strings.Get("可编辑草稿，连接后手动发送");
    public bool CanStartConnection => !_resettingIdentity && !_isDialing && _sessions.CanAccept;
    public bool CanCancelConnection => _isDialing;
    public bool CanConnectSelected => SelectedDevice?.CanInitiate == true && CanStartConnection && !IsBluetoothUnavailable;
    public string ConnectActionText => SelectedDevice is null ? Localization.Strings.Get("选择附近设备") :
        CanConnectSelected && SelectedDevice.Platform == PeerPlatform.Android ? Localization.Strings.Get("连接 Android 设备") :
        CanConnectSelected ? Localization.Strings.Get("连接所选设备") : Localization.Strings.Get("等待对端开放连接");

    internal void UseVisualFixture(ConversationSummary conversation)
    {
        _activePeerId = conversation.PeerId;
        _activeSessionId = conversation.SessionId;
        Status = conversation.PeerName;
        Detail = Localization.Strings.Get("端到端加密");
        Phase = ConnectionPhase.Connected;
        Raise(nameof(HasActiveConversation));
        Raise(nameof(ActiveSessionCount));
        Raise(nameof(ActiveSessionCountText));
    }

    public async Task InitializeLocalStateAsync()
    {
        _database.AssociateComposerDrafts = legacy =>
        {
            _recovery.Associate(_identity.IdentityAssociations);
            return ComposerStore.Associate(_identity.IdentityAssociations, legacy);
        };
        await _database.InitializeAsync(_identity);
        Settings = await _database.LoadSettingsAsync();
        _sessions.UsbEnabled = Settings.UsbEnabled;
        Appearance.AppearanceService.Apply(Settings);
        Updates.RefreshText();
        // Existing bindings may have evaluated before the persisted language was applied.
        Raise(string.Empty);
        _bluetooth.LocalDeviceName = LocalDeviceDisplayName;
        _sessions.LocalDeviceName = LocalDeviceDisplayName;
        SessionLog.Enabled = Settings.DiagnosticsEnabled;
        await ApplyRetentionAsync(Settings.RetentionPeriod);
        foreach (var peer in await _database.LoadPeersAsync()) _storedPeers[peer.PeerId] = peer;
        foreach (var conversation in await _database.LoadConversationsAsync())
            _storedConversations[conversation.PeerId] = conversation;
        LoadDrafts();
        AllTransfers.Clear();
        foreach (var transfer in await LoadStoredTransfersAsync(null)) AllTransfers.Add(transfer);
        RefreshConversations();
        foreach (var conversation in Conversations)
            Notifications.Restore(conversation.PeerId, conversation.PeerName, conversation.UnreadCount, conversation.LastActivityAt);
        try {
            var references = (await _database.LoadReferencedFilePathsAsync()).Concat(_recovery.MergeHistory([])
                .Where(x => x.LocalPath is not null).Select(x => Path.GetFullPath(x.LocalPath!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Task.Run(() => {
                OwnedTemporaryFiles.Collect(Path.Combine(CacheDirectory,"Outgoing"), _recovery.ReferencesTemporary, preview:false, minimumAge:TimeSpan.FromDays(1));
                ComposerStore.CollectStartupOrphans(references);
            });
        }
        catch (Exception error) { SessionLog.Write("Storage", "暂未清理旧发送缓存", error); }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await InitializeLocalStateAsync();
            _sessions.ReceiveDirectory = Settings.DownloadDirectory;
            _sessions.MaxReceiveBytes = Settings.ReceiveSizeLimitEnabled ? Settings.ReceiveSizeLimitBytes : long.MaxValue;
            _sessions.AutoAcceptFiles = Settings.AutoDownloadFiles;
        _sessions.DuplicateFilePolicy = Settings.DuplicateFilePolicy;
        _bluetooth.AllowDiscovery = Settings.AllowDiscovery;
            _runtimeStarted = true;
            await ObserveBluetoothAsync();
            _bluetooth.ConnectionAccepted += AcceptConnectionAsync;
            _bluetooth.DevicesChanged += OnDevicesChanged;
            await _bluetooth.StartAsync();
            _reconnectLoop ??= RunReconnectLoopAsync(_lifetime.Token);
            Status = string.IsNullOrWhiteSpace(_bluetooth.StartupWarning) ? Localization.Strings.Get("蓝牙就绪") : Localization.Strings.Get("蓝牙部分可用");
            if (Settings.ScanOnStartup) await ScanAsync();
            else Detail = Localization.Strings.Get("启动扫描已关闭");
        }
        catch (Exception failure) { SetState(ConnectionPhase.Offline, Localization.Strings.Get("蓝牙不可用"), failure.Message); }
        finally { if (_runtimeStarted) _sessions.UsbEnabled = Settings.UsbEnabled; }
    }

    public async Task ScanAsync()
    {
        if (!CanScan) return;
        IsScanning = true;
        _scanCompleted = false;
        _scanFailed = false;
        ScanFeedback = Localization.Strings.Get("正在扫描附近运行蓝联的设备…");
        SetState(Phase, Status, Localization.Strings.Get("正在扫描附近运行蓝联的设备…"));
        try
        {
            var devices = await (ScanForAcceptance?.Invoke() ?? _bluetooth.ScanAsync());
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ApplyDevices(devices);
                RefreshConversations();
                var summary = devices.Count == 0
                    ? Localization.Strings.Get("附近没有发现运行蓝联的设备")
                    : Localization.Strings.Format($"发现 {devices.Count} 台附近 BlueLink 设备");
                Detail = string.IsNullOrWhiteSpace(_bluetooth.StartupWarning)
                    ? summary
                    : $"{summary}；{_bluetooth.StartupWarning}";
                _scanCompleted = true;
                RefreshCompletedScanFeedback();
            });
        }
        catch (Exception failure)
        {
            Detail = failure.Message;
            _scanFailed = true;
            ReportScanFailure(failure);
        }
        finally { IsScanning = false; RefreshCompletedScanFeedback(); }
    }

    private void RefreshCompletedScanFeedback()
    {
        if (!_scanCompleted || _scanFailed || IsScanning) return;
        // Known peers remain in history, but still count as real discovery results.
        ScanFeedback = Devices.Count == 0
            ? Localization.Strings.Get("扫描完成，未发现附近设备")
            : Localization.Strings.Format($"扫描完成，发现 {Devices.Count} 台设备（新设备 {NearbyNewDevices.Count} 台）");
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
                ? Localization.Strings.Get("正在扫描附近运行蓝联的设备…")
                : Localization.Strings.Format($"正在扫描 · 已发现 {devices.Count} 台附近 BlueLink 设备");
        });
    }

    private void ApplyDevices(IReadOnlyList<NearbyDevice> devices)
    {
        if (IsBluetoothUnavailable) devices = [];
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
        if (!automatic)
            foreach (var peer in _storedPeers.Values.Where(peer => MatchesPeer(device, peer.PeerId) ||
                string.Equals(peer.TransportAddress, device.Address, StringComparison.OrdinalIgnoreCase)))
                _manuallyDisconnected.TryRemove(peer.PeerId, out _);
        if (!device.CanInitiate || IsBluetoothUnavailable || _isDialing || !_sessions.CanAccept ||
            Sessions.Any(session => NormalizeAddress(session.TransportAddress) == NormalizeAddress(device.Address))) return;
        _isDialing = true;
        _dialCanceledByUser = false;
        SessionLog.Write("Connection", $"Starting {(automatic ? "automatic" : "manual")} Bluetooth connection");
        _dialCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected)); Raise(nameof(CanCancelConnection));
        UpdateConnectionAttempt(device.Address, ConnectionPhase.Connecting, Localization.Strings.Get("正在建立安全连接"));
        if (!HasActiveConversation) SetState(ConnectionPhase.Connecting, device.Name,
            device.Platform == PeerPlatform.Android
                ? Localization.Strings.Get("正在通过 GATT 请求 Android 建立 RFCOMM 回连")
                : Localization.Strings.Get("正在建立 RFCOMM 通道"));
        try
        {
            var connection = await _bluetooth.ConnectAsync(device, _dialCancellation.Token);
            await StartSessionAsync(connection, connection.ListenerRole, select: !automatic,
                transportAddress: device.Address);
        }
        catch (OperationCanceledException failure)
        {
            if (automatic) RegisterReconnectFailure(device.Address);
            UpdateConnectionAttempt(device.Address, ConnectionPhase.Disconnected, Localization.Strings.Get("本次连接已取消或超时"));
            ReportConnectionFailure(device, failure, automatic);
            if (!HasActiveConversation) SetState(ConnectionPhase.Disconnected, device.Name, Localization.Strings.Get("本次连接已取消或超时"));
        }
        catch (Exception failure)
        {
            if (automatic) RegisterReconnectFailure(device.Address);
            UpdateConnectionAttempt(device.Address, ConnectionPhase.Disconnected, failure.Message);
            ReportConnectionFailure(device, failure, automatic);
            if (!HasActiveConversation) SetState(ConnectionPhase.Disconnected, Localization.Strings.Get("连接失败"), failure.Message);
        }
        finally
        {
            _dialCancellation?.Dispose();
            _dialCancellation = null;
            _isDialing = false;
            Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected)); Raise(nameof(CanCancelConnection));
        }
    }

    public void CancelConnection()
    {
        _dialCanceledByUser = true;
        _dialCancellation?.Cancel();
    }

    public Task<bool> SendAsync(string text) => _activeSessionId is { } sessionId && IsConnected
        ? SendTextToSessionAsync(sessionId, text) : Task.FromResult(false);

    internal Task<bool> SendComposerTextAsync(string peer, string text)
    {
        var route = Sessions.Where(value => value.Phase == ConnectionPhase.Connected && string.Equals(value.PeerId, peer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => Settings.UsbEnabled && value.Transport == TransportKind.Usb).FirstOrDefault();
        return route is null ? Task.FromResult(false) : SendTextToSessionAsync(route.SessionId, text);
    }

    private async Task<bool> SendTextToSessionAsync(Guid sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var item = new ChatItem(Guid.NewGuid(), text.Trim(), true, DateTimeOffset.Now, MessageStatus.Sending);
        if (!_sessionMessages.TryGetValue(sessionId, out var sessionItems))
            _sessionMessages[sessionId] = sessionItems = [];
        sessionItems.Add(item);
        var snapshot = Sessions.FirstOrDefault(value => value.SessionId == sessionId);
        if (string.Equals(_activePeerId, snapshot?.PeerId, StringComparison.OrdinalIgnoreCase)) Messages.Add(item);
        if (snapshot?.PeerId is { } peerId) await PersistMessageAsync(snapshot, item, unread: false);
        try
        {
            await _sessions.SendChatAsync(sessionId, item.Text, item.Id);
            var sent = item with { Status = MessageStatus.Sent };
            ReplaceSessionMessage(sessionId, sent);
            ReplaceMessage(sent);
            if (snapshot?.PeerId is not null) await PersistMessageAsync(snapshot, sent, unread: false);
            return true;
        }
        catch
        {
            var failed = item with { Status = MessageStatus.Failed };
            ReplaceSessionMessage(sessionId, failed);
            ReplaceMessage(failed);
            if (snapshot?.PeerId is not null) await PersistMessageAsync(snapshot, failed, unread: false);
            return false;
        }
    }

    internal Task SendComposerFileAsync(string peerId, string path, string? displayName = null)
    {
        var route = Sessions.Where(value => value.Phase == ConnectionPhase.Connected &&
            string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => Settings.UsbEnabled && value.Transport == TransportKind.Usb).FirstOrDefault();
        if (route is null) return Task.FromException(new IOException(Localization.Strings.Get("设备未连接，无法发送文件")));
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveComposerFileAsync(_sessions.SendFileAsync(route.SessionId, path, displayName, () => announced.TrySetResult()), announced);
        return announced.Task;
    }

    private static async Task ObserveComposerFileAsync(Task transfer, TaskCompletionSource announced)
    {
        try { await transfer; announced.TrySetResult(); }
        catch (Exception error) { announced.TrySetException(error); } // Announced files keep their transfer-row retry action.
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
        if (!transfer.CanRetry || string.IsNullOrWhiteSpace(transfer.LocalPath))
            return Task.FromException(new InvalidOperationException("原文件不可读取或任务不可重试"));
        var sessionId = ResolveSessionId(transfer);
        return sessionId is { } id ? _sessions.RetryFileAsync(id, transfer.LocalPath, transfer)
            : Task.FromException(new InvalidOperationException("设备当前未连接"));
    }

    internal Task SwitchQueuedToBluetoothAsync(TransferItem transfer) => ResolveSessionId(transfer) is { } session
        ? _sessions.SwitchQueuedToBluetoothAsync(session, transfer)
        : Task.FromException(new InvalidOperationException("设备当前未连接"));

    internal Task RetryTransferFromAsync(TransferItem transfer, string path)
    {
        var current = AllTransfers.FirstOrDefault(item => item.Id == transfer.Id);
        if (current is null || !current.CanReselectSource || !string.Equals(current.PeerId, transfer.PeerId, StringComparison.OrdinalIgnoreCase))
            return Task.FromException(new InvalidOperationException("任务已变化或缺少原文件指纹，请重新选择文件创建新任务。"));
        var session = ResolveSessionId(current);
        if (session is null) return Task.FromException(new InvalidOperationException("设备当前未连接"));
        var candidate = current.Snapshot(); candidate.LocalPath = path;
        // The existing snapshot/hash validation runs before publication or network transfer.
        return _sessions.RetryFileAsync(session.Value, path, candidate);
    }

    private Guid? ResolveSessionId(TransferItem transfer)
    {
        if (!string.IsNullOrWhiteSpace(transfer.PeerId))
            return Sessions.FirstOrDefault(value => value.Phase == ConnectionPhase.Connected && string.Equals(value.PeerId, transfer.PeerId,
                StringComparison.OrdinalIgnoreCase))?.SessionId;
        return null;
    }

    private async Task StartSessionAsync(IPeerConnection connection, bool listenerRole, bool select = false,
        string? transportAddress = null)
    {
        if (_resettingIdentity || Volatile.Read(ref _disposeStarted) != 0)
        {
            await connection.DisposeAsync();
            return;
        }
        var address = transportAddress ?? connection.TransportAddress;
        var expectedPeer = _storedPeers.Values.FirstOrDefault(peer =>
            !string.IsNullOrEmpty(address) && NormalizeAddress(connection.Transport == TransportKind.Usb ? peer.UsbTransportAddress : peer.TransportAddress) == NormalizeAddress(address) &&
            _identity.FindTrustedKey(peer.PeerId) is not null);
        var sessionId = await _sessions.AddAsync(connection, listenerRole, transportAddress, expectedPeer?.PeerId);
        if (sessionId is null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateConnectionAttempt(
                transportAddress ?? connection.TransportAddress, ConnectionPhase.Disconnected,
                Localization.Strings.Get("连接已停止，请重试")));
            return;
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (select || _activeSessionId is null) SelectSession(sessionId.Value);
            if (_activeSessionId == sessionId.Value)
                SetState(ConnectionPhase.SecureHandshake, connection.PeerName, Localization.Strings.Get("正在验证设备身份"));
        });
    }

    public void SelectSession(Guid sessionId)
    {
        _conversationSelectionVersion++;
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
                if (_windowHasFocus && !IsSettingsOpen && !ShowFiles) _ = MarkConversationReadAsync(selected.PeerId);
                _ = LoadHistoryAsync(selected.PeerId, sessionId);
            }
        }
    }

    public async Task SelectConversationAsync(string peerId)
    {
        _ = FlushDraftsAsync();
        var selectionVersion = ++_conversationSelectionVersion;
        _activePeerId = peerId;
        var session = Sessions.FirstOrDefault(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (session is not null) { SelectSession(session.SessionId); return; }
        _activeSessionId = null;
        Messages.Clear();
        Transfers.Clear();
        var peer = _storedPeers.GetValueOrDefault(peerId);
        SetState(ConnectionPhase.Offline, peer?.DisplayName ?? Localization.Strings.Get("离线设备"), Localization.Strings.Get("设备离线 · 可查看历史记录"));
        RaiseActiveConversationState();
        if (_windowHasFocus && !IsSettingsOpen && !ShowFiles) await MarkConversationReadAsync(peerId);
        if (selectionVersion != _conversationSelectionVersion) return;
        var messages = await LoadStoredMessagesAsync(peerId);
        if (selectionVersion != _conversationSelectionVersion) return;
        var transfers = await LoadStoredTransfersAsync(peerId);
        // An earlier selection may finish loading after a newer click, including A -> B -> A.
        if (selectionVersion != _conversationSelectionVersion) return;
        foreach (var item in messages) Messages.Add(item);
        foreach (var item in transfers) Transfers.Add(item);
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
        _manuallyDisconnected[conversation.PeerId] = 0;
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
        await ClearDraftsAsync(peerId);
        await _conversationPersistenceGate.WaitAsync();
        try
        {
        await _database.ClearConversationMessagesAsync(ConversationId(peerId));
        foreach (var session in Sessions.Where(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase)))
            if (_sessionMessages.TryGetValue(session.SessionId, out var values)) values.Clear();
        if (string.Equals(_activePeerId, peerId, StringComparison.OrdinalIgnoreCase)) Messages.Clear();
        if (_storedConversations.TryGetValue(peerId, out var conversation)) _storedConversations[peerId] = conversation with { UnreadCount = 0 };
        Notifications.Read(peerId); RefreshConversations();
        }
        finally { _conversationPersistenceGate.Release(); }
    }

    public async Task ExportDiagnosticsAsync(string targetPath)
    {
        var value = await Task.Run(() => DiagnosticsSnapshot.ReadAsync(DiagnosticsPath));
        await File.WriteAllTextAsync(targetPath, value);
    }

    private Task ApplyRetentionAsync(string retention)
    {
        var age = retention switch { "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), "90d" => TimeSpan.FromDays(90),
            "1y" => TimeSpan.FromDays(365), _ => (TimeSpan?)null };
        return age is null ? Task.CompletedTask : _database.DeleteHistoryBeforeAsync(
            DateTimeOffset.UtcNow.Subtract(age.Value).ToUnixTimeMilliseconds());
    }

    public async Task SaveSettingsAsync(BlueLinkSettings value)
    {
        var saved = value with
        {
            Theme = Appearance.AppearancePreferences.NormalizeTheme(value.Theme),
            Language = Appearance.AppearancePreferences.NormalizeLanguage(value.Language),
            SendShortcut = ComposerShortcuts.Normalize(value.SendShortcut),
        };
        await _database.SaveSettingsAsync(saved);
        Settings = saved;
        _sessions.UsbEnabled = saved.UsbEnabled;
        Appearance.AppearanceService.Apply(Settings);
        Updates.RefreshText();
        RefreshConversations();
        Raise(string.Empty);
        ConnectedDevicesView.Refresh();
        OfflineDevicesView.Refresh();
        NearbyDevicesView.Refresh();
        MessagesView.Refresh();
        foreach (var transfer in AllTransfers) transfer.RefreshLocalizedText();
        _bluetooth.LocalDeviceName = LocalDeviceDisplayName;
        _sessions.LocalDeviceName = LocalDeviceDisplayName;
        Raise(nameof(LocalDeviceDisplayName));
        _sessions.ReceiveDirectory = Settings.DownloadDirectory;
        _sessions.MaxReceiveBytes = Settings.ReceiveSizeLimitEnabled ? Settings.ReceiveSizeLimitBytes : long.MaxValue;
        _sessions.AutoAcceptFiles = Settings.AutoDownloadFiles;
        _sessions.DuplicateFilePolicy = Settings.DuplicateFilePolicy;
        await _bluetooth.SetDiscoveryAsync(Settings.AllowDiscovery);
        SessionLog.Enabled = Settings.DiagnosticsEnabled;
        await ApplyRetentionAsync(Settings.RetentionPeriod);
        if (_runtimeStarted)
        {
            if (!Settings.UsbEnabled) await _sessions.DisconnectTransportAsync(TransportKind.Usb);
            _sessions.UsbEnabled = Settings.UsbEnabled;
        }
        Raise(nameof(ActiveUsbReady)); RaiseUsbNotice();
    }

    private Task AcceptConnectionAsync(RfcommConnection connection) => AcceptTransportAsync(connection);
    private async Task AcceptTransportAsync(IPeerConnection connection)
    {
        if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher || Volatile.Read(ref _disposeStarted) != 0)
        { await connection.DisposeAsync(); return; }
        await dispatcher.InvokeAsync(() => StartSessionAsync(connection, connection.ListenerRole)).Task.Unwrap();
    }
    public async Task<string?> ResetIdentityAsync()
    {
        if (_resettingIdentity || Volatile.Read(ref _disposeStarted) != 0) throw new InvalidOperationException(Localization.Strings.Get("设备身份正在更新，请稍后重试。"));
        _resettingIdentity = true;
        _dialCancellation?.Cancel();
        Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected));
        string? warning = null;
        try
        {
            await _sessions.SuspendAsync();
            _sessions.UsbEnabled = false;
            _bluetooth.ConnectionAccepted -= AcceptConnectionAsync;
            _bluetooth.DevicesChanged -= OnDevicesChanged;
            _bluetooth.Dispose();
            await _trustMutationGate.WaitAsync();
            try
            {
                _identity.ResetIdentity();
                foreach (var peer in _storedPeers.Values.Where(peer => peer.TrustState != StoredTrustState.Removed).ToArray())
                    _storedPeers[peer.PeerId] = peer with { TrustState = StoredTrustState.Unknown, IdentityPublicKey = null };
                try { await _database.ClearPeerTrustAsync(); }
                catch (Exception failure) { warning = Localization.Strings.Format($"身份已重置，但本地信任记录同步失败：{failure.Message}"); }
            }
            finally { _trustMutationGate.Release(); }
        }
        finally
        {
            _bluetooth = new RfcommBluetoothService(_identity.Identity.PublicKey) { LocalDeviceName = LocalDeviceDisplayName, AllowDiscovery = Settings.AllowDiscovery };
            if (_runtimeStarted && Volatile.Read(ref _disposeStarted) == 0)
            {
                _bluetooth.ConnectionAccepted += AcceptConnectionAsync;
                _bluetooth.DevicesChanged += OnDevicesChanged;
                try { await _bluetooth.StartAsync(); }
                catch (Exception failure) { warning = Localization.Strings.Format($"身份已更新，蓝牙服务暂不可用：{failure.Message}"); }
            }
            _sessions.Resume();
            _resettingIdentity = false;
            if (_runtimeStarted && Volatile.Read(ref _disposeStarted) == 0) _sessions.UsbEnabled = Settings.UsbEnabled;
            _connectionAttempts.Clear();
            Devices.Clear();
            RefreshConversations();
            Raise(nameof(IdentityFingerprint)); Raise(nameof(CanStartConnection)); Raise(nameof(CanConnectSelected));
        }
        return warning;
    }

    public void RefreshUsbDiscovery() => _sessions.RequestMtpProbe();

    public async Task ForgetPeerAsync(string peerId)
    {
        var revoked = false;
        await _trustMutationGate.WaitAsync();
        try
        {
            _identity.RemoveTrust(peerId);
            revoked = true;
            if (_storedPeers.TryGetValue(peerId, out var peer))
                _storedPeers[peerId] = peer with { TrustState = StoredTrustState.Removed, IdentityPublicKey = null };
            try { await _database.RevokePeerTrustAsync(peerId); }
            catch (Exception failure) { throw new IOException(Localization.Strings.Get("信任已移除，但本地记录同步失败；重新启动后会再次同步。"), failure); }
        }
        finally
        {
            _trustMutationGate.Release();
            if (revoked)
                foreach (var session in _sessions.Snapshot().Where(value => string.Equals(value.PeerId, peerId, StringComparison.OrdinalIgnoreCase)))
                    await _sessions.DisconnectAsync(session.SessionId);
            RefreshConversations();
        }
    }

    public async Task ForgetAllPeersAsync()
    {
        foreach (var peer in _identity.TrustedIdentities) await ForgetPeerAsync(peer.PeerIdHex);
    }

    public async Task ClearChatHistoryAsync()
    {
        await ClearDraftsAsync();
        await _conversationPersistenceGate.WaitAsync();
        try
        {
        await _database.ClearMessagesAsync();
        foreach (var values in _sessionMessages.Values) values.Clear();
        Messages.Clear();
        foreach (var peer in _storedConversations.Keys.ToArray()) _storedConversations[peer] = _storedConversations[peer] with { UnreadCount = 0 };
        Notifications.Clear(); RefreshConversations();
        }
        finally { _conversationPersistenceGate.Release(); }
    }

    public async Task ClearTransferHistoryAsync()
    {
        await _database.ClearTransfersAsync();
        // Clearing history does not discard operational recovery or an active transfer.
        foreach (var values in _sessionTransfers.Values)
            foreach (var id in values.Where(x => !x.Value.IsActive && !x.Value.RecoveryPending).Select(x => x.Key).ToArray()) values.Remove(id);
        foreach (var item in Transfers.Where(x => !x.IsActive && !x.RecoveryPending).ToArray()) Transfers.Remove(item);
        foreach (var item in AllTransfers.Where(x => !x.IsActive && !x.RecoveryPending).ToArray())
        {
            if (_recovery.Dismiss(item.Id)) AllTransfers.Remove(item);
        }
    }

    public async Task DeleteTransferAsync(TransferItem transfer)
    {
        if (!transfer.CanDelete || !_recovery.Dismiss(transfer.Id)) return;
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
        if (snapshot.Phase == ConnectionPhase.Connected && snapshot.PeerId is { } connectedPeer)
        {
            _connectedThisRun[connectedPeer] = 0;
            if (_notifiedConnections.Add(snapshot.SessionId) && Settings.ConnectionNotifications)
                SystemNotificationRequested?.Invoke(Localization.Strings.Get("设备已连接"), snapshot.PeerName);
        }
        else if (snapshot.Phase == ConnectionPhase.Disconnected && _notifiedConnections.Remove(snapshot.SessionId) && Settings.ConnectionNotifications)
            SystemNotificationRequested?.Invoke(Localization.Strings.Get("设备已断开"), snapshot.PeerName);
        if (_connectionAttempts.ContainsKey(NormalizeAddress(snapshot.TransportAddress)))
            UpdateConnectionAttempt(snapshot.TransportAddress, snapshot.Phase, snapshot.Detail);
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
        if (!message.Outgoing && snapshot.PeerId is { } peerId) Notifications.Receive(peerId, snapshot.PeerName, message.Id, message.Text, IsConversationVisible(peerId));
        if (!_sessionMessages.TryGetValue(snapshot.SessionId, out var values)) _sessionMessages[snapshot.SessionId] = values = [];
        values.Add(message);
        if (_activeSessionId == snapshot.SessionId) Messages.Add(message);
        _ = PersistMessageAsync(snapshot, message, unread: !message.Outgoing && !IsConversationVisible(snapshot.PeerId));
    });

    private void OnSessionEnvelope(SessionSnapshot snapshot, ChatEnvelope envelope, bool outgoing) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (envelope.Attachments.Count == 0) return;
            if (!outgoing && snapshot.PeerId is { } peerId) Notifications.Receive(peerId, snapshot.PeerName, envelope.MessageId,
                envelope.Kind == ChatPayloadKind.Image ? Localization.Strings.Get("收到图片") : Localization.Strings.Get("收到文件"), IsConversationVisible(peerId));
            _ = PersistEnvelopeAsync(snapshot, envelope, outgoing,
                unread: !outgoing && !IsConversationVisible(snapshot.PeerId));
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

    private readonly SessionTransferLedger _transferProjection = new();
    private void OnSessionTransfer(SessionSnapshot snapshot, TransferItem transfer) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (transfer.AttemptId is not null && _transferProjection.Record(transfer) is null) return;
        if (transfer.Status == TransferStatus.Completed && transfer.Role != AttachmentRole.ImagePreview && _notifiedTransfers.Add(transfer.Id) && Settings.TransferNotifications)
            SystemNotificationRequested?.Invoke(Localization.Strings.Get(transfer.Outgoing ? "文件发送完成" : "文件接收完成"), transfer.Name);
        transfer.PeerId = snapshot.PeerId;
        transfer.PeerName = snapshot.PeerName;
        if (!transfer.Outgoing && transfer.Status == TransferStatus.Completed && transfer.Role != AttachmentRole.ImagePreview && snapshot.PeerId is { } peerId)
            Notifications.UpdatePreview(peerId, Localization.Strings.Get("文件接收完成") + " · " + transfer.Name);
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
        var globalIndex = AllTransfers.ToList().FindIndex(item => item.Id == transfer.Id);
        if (globalIndex < 0) AllTransfers.Insert(0, transfer);
        else if (!ReferenceEquals(AllTransfers[globalIndex], transfer)) AllTransfers[globalIndex] = transfer;
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
            } : value.WithTransfer(transfer)).ToArray();
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

    private void PresentTrustRequest(TrustRequest request)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (request.Stage is TrustStage.Completed or TrustStage.Canceled || Volatile.Read(ref _disposeStarted) != 0) return;
            request.RetryAvailable = Devices.Any(value => value.CanInitiate && !string.IsNullOrEmpty(request.TransportAddress) &&
                NormalizeAddress(value.Address) == NormalizeAddress(request.TransportAddress));
            request.PeerPlatform = Devices.FirstOrDefault(value => !string.IsNullOrEmpty(request.TransportAddress) &&
                NormalizeAddress(value.Address) == NormalizeAddress(request.TransportAddress))?.Platform ?? PeerPlatform.Unknown;
            var window = new TrustConfirmationWindow(request)
            {
                Owner = Application.Current.MainWindow
            };
            using (window.Owner is { } owner ? BlueLinkDialog.DimOwner(owner, "#48101828", 52) : null)
                window.ShowDialog();
            if (window.ManageTrustRequested)
            {
                if (Application.Current.MainWindow is MainWindow main) main.OpenSettings(connections: true);
            }
            if (window.RetryRequested)
            {
                var device = Devices.FirstOrDefault(value => value.CanInitiate && !string.IsNullOrEmpty(request.TransportAddress) &&
                    NormalizeAddress(value.Address) == NormalizeAddress(request.TransportAddress));
                if (device is not null) await ConnectDeviceAsync(device, automatic: false);
            }
        }));
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
                SourceSha256 = value.SourceSha256, AttemptId = value.AttemptId, AttemptSequence = value.AttemptSequence,
                Role = value.Role, RecoveryPending = value.RecoveryPending });
        }
        else
        {
            current.RecoveryPending = value.RecoveryPending;
            current.CompletedBytes = value.CompletedBytes; current.Status = value.Status;
            current.MessageId = value.MessageId ?? current.MessageId;
            current.AttachmentId = value.AttachmentId ?? current.AttachmentId;
            current.MimeType = value.MimeType;
            current.Role = value.Role;
            current.LocalPath = value.LocalPath ?? current.LocalPath;
            current.SourceSha256 = value.SourceSha256 ?? current.SourceSha256;
            current.AttemptId = value.AttemptId ?? current.AttemptId;
            current.AttemptSequence = Math.Max(current.AttemptSequence, value.AttemptSequence);
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
        if (snapshot.PeerId is not { } peerId || _identity.IsRetired(peerId)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (snapshot.Phase == ConnectionPhase.Connected)
        {
            await _trustMutationGate.WaitAsync();
            try
            {
            if (_resettingIdentity || !IsCurrentTrustedSession(snapshot)) return;
            var previous = _storedPeers.GetValueOrDefault(peerId);
            var key = _identity.FindTrustedKey(peerId);
            var peer = new StoredPeer(peerId, !snapshot.HasPeerProvidedName && (snapshot.Transport == TransportKind.Usb || _identity.IdentityAssociations.Values.Contains(peerId, StringComparer.OrdinalIgnoreCase)) && !string.IsNullOrEmpty(previous?.DisplayName) ? previous.DisplayName : snapshot.PeerName,
                snapshot.Platform == PeerPlatform.Unknown ? previous?.Platform ?? "Unknown" : snapshot.Platform.ToString(), StoredTrustState.Trusted,
                key, previous?.CreatedAt ?? now, now, now,
                snapshot.Transport == TransportKind.Bluetooth ? snapshot.TransportAddress : previous?.TransportAddress ?? "",
                snapshot.Transport == TransportKind.Usb ? snapshot.TransportAddress : previous?.UsbTransportAddress ?? "");
            await _database.UpsertPeerAsync(peer);
            foreach (var hint in _sessions.IdentityHints(peerId)) await _database.RecordIdentityHintAsync(peerId, hint);
            _storedPeers[peerId] = peer;
            var conversation = _storedConversations.GetValueOrDefault(peerId) ??
                new StoredConversation(ConversationId(peerId), peerId, now, 0);
            await _database.UpsertConversationMetadataAsync(conversation);
            _storedConversations[peerId] = conversation;
            _reconnectAfter.Remove(snapshot.TransportAddress);
            await _database.UpsertSessionRecordAsync(new(snapshot.SessionId.ToString("N"), peerId,
                snapshot.StartedAt.ToUnixTimeMilliseconds(), "Connected", now, null, null));
            }
            finally { _trustMutationGate.Release(); }
            await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
            await LoadHistoryAsync(peerId, snapshot.SessionId);
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
        if (snapshot.PeerId is not { } peerId || _identity.IsRetired(peerId)) return;
        await _trustMutationGate.WaitAsync();
        try
        {
        if (!_storedPeers.TryGetValue(peerId, out var peer))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var key = _identity.FindTrustedKey(peerId);
            peer = new StoredPeer(peerId, snapshot.PeerName, snapshot.Platform.ToString(), key is null ? StoredTrustState.Unknown : StoredTrustState.Trusted, key,
                now, now, now, snapshot.Transport == TransportKind.Bluetooth ? snapshot.TransportAddress : "",
                snapshot.Transport == TransportKind.Usb ? snapshot.TransportAddress : "");
            await _database.UpsertPeerAsync(peer);
            foreach (var hint in _sessions.IdentityHints(peerId)) await _database.RecordIdentityHintAsync(peerId, hint);
            _storedPeers[peerId] = peer;
        }
        if (!_storedConversations.ContainsKey(peerId))
        {
            var conversation = new StoredConversation(ConversationId(peerId), peerId, activityAt, 0);
            await _database.UpsertConversationMetadataAsync(conversation);
            _storedConversations[peerId] = conversation;
        }
        }
        finally { _trustMutationGate.Release(); }
    }

    private bool IsCurrentTrustedSession(SessionSnapshot snapshot) => snapshot.PeerId is { } peerId &&
        _identity.FindTrustedKey(peerId) is not null && _sessions.Snapshot().Any(value =>
            value.SessionId == snapshot.SessionId && value.Phase == ConnectionPhase.Connected);

    private async Task PersistMessageAsync(SessionSnapshot snapshot, ChatItem item, bool unread)
    {
        if (!Settings.SaveChatHistory || snapshot.PeerId is not { } peerId || _identity.IsRetired(peerId)) return;
        await _conversationPersistenceGate.WaitAsync();
        try
        {
        if (_identity.IsRetired(peerId)) return;
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
            UnreadCount = current.UnreadCount + (unread && !IsConversationVisible(peerId) ? 1 : 0) };
        await _database.UpsertConversationMetadataAsync(current);
        _storedConversations[peerId] = current;
        await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
        }
        finally { _conversationPersistenceGate.Release(); }
    }

    private async Task PersistEnvelopeAsync(SessionSnapshot snapshot, ChatEnvelope envelope, bool outgoing, bool unread)
    {
        if (!Settings.SaveChatHistory || snapshot.PeerId is not { } peerId || _identity.IsRetired(peerId)) return;
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
        if (!Settings.SaveTransferHistory || snapshot.PeerId is not { } peerId || _identity.IsRetired(peerId)) return;
        // Capture this event before waiting; history writes must never mutate a live UI snapshot.
        transfer = transfer.Snapshot();
        transfer.PeerName = snapshot.PeerName;
        transfer.PeerId = peerId;
        await _transferPersistenceGate.WaitAsync();
        try
        {
        if (_identity.IsRetired(peerId)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await EnsureConversationAsync(snapshot, now);
        var previous = (await _database.LoadTransfersAsync(peerId)).FirstOrDefault(value =>
            value.TransferId.Equals(transfer.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
        await _database.UpsertTransferAsync(new(transfer.Id.ToString("N"), peerId,
            transfer.MessageId?.ToString("N") ?? previous?.MessageId,
            transfer.Outgoing ? "Outgoing" : "Incoming", transfer.Status.ToString(), transfer.Name,
            transfer.MimeType, transfer.TotalBytes, transfer.CompletedBytes, transfer.LocalPath ?? previous?.LocalPath,
            previous?.SnapshotPath, transfer.SourceSha256 is { } hash ? Convert.FromHexString(hash) : previous?.Sha256, transfer.Status == TransferStatus.Failed ? "TRANSFER_FAILED" : null,
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
        }
        finally { _transferPersistenceGate.Release(); }
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
        await _historyGate.WaitAsync();
        try
        {
        if (_historyLoaded.Contains(sessionId)) return;
        var persisted = await LoadStoredMessagesAsync(peerId);
        var transient = _sessionMessages.GetValueOrDefault(sessionId) ?? [];
        var merged = persisted.Concat(transient).GroupBy(value => value.Id).Select(group => group.Last())
            .OrderBy(value => value.CreatedAt).ToList();
        _sessionMessages[sessionId] = merged;
        var transfers = await LoadStoredTransfersAsync(peerId);
        transfers = transfers.Concat(_sessionTransfers.GetValueOrDefault(sessionId)?.Values ?? Enumerable.Empty<TransferItem>())
            .GroupBy(value => value.Id).Select(group => group.Last()).ToList();
        _sessionTransfers[sessionId] = transfers.ToDictionary(value => value.Id);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_activeSessionId != sessionId) return;
            Messages.Clear(); foreach (var item in merged) Messages.Add(item);
            Transfers.Clear(); foreach (var item in transfers) Transfers.Add(item);
        });
        _historyLoaded.Add(sessionId);
        }
        finally { _historyGate.Release(); }
    }

    private async Task<List<ChatItem>> LoadStoredMessagesAsync(string peerId, string? anchorId = null, bool around = false, bool all = false)
    {
        var result = new List<ChatItem>();
        var page = all ? null : await _database.LoadHistoryPageAsync(ConversationId(peerId), anchorId, around);
        var stored = page?.Messages ?? await _database.LoadMessagesAsync(ConversationId(peerId));
        var attachmentLookup = (page?.Attachments ?? await _database.LoadConversationAttachmentsAsync(ConversationId(peerId))).ToLookup(item => item.MessageId);
        var transferLookup = AllTransfers.Where(item => string.Equals(item.PeerId, peerId, StringComparison.OrdinalIgnoreCase)).ToDictionary(item => item.Id.ToString("N"), item => item.Snapshot(), StringComparer.OrdinalIgnoreCase);
        return await Task.Run(() =>
        {
        foreach (var value in stored)
        {
            var attachments = attachmentLookup[value.MessageId].Select(item =>
                new ChatAttachment(Guid.ParseExact(item.AttachmentId, "N"),
                    item.TransferId is null ? Guid.Empty : Guid.ParseExact(item.TransferId, "N"),
                    item.FileName, item.MimeType, item.Size, item.LocalPath, RestoredAttachmentState(item.TransferId, item.State, transferLookup), item.PreviewPath,
                    item.State == "Completed" ? item.Size : 0).WithTransfer(transferLookup.GetValueOrDefault(item.TransferId ?? ""))).ToArray();
            _ = Enum.TryParse<MessageStatus>(value.Status, true, out var status);
            result.Add(new(Guid.ParseExact(value.MessageId, "N"), value.Content,
                value.Direction == StoredMessageDirection.Outgoing, DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAt),
                status, value.Type switch { StoredMessageType.Image => ChatItemKind.Image,
                    StoredMessageType.File => ChatItemKind.File, StoredMessageType.System => ChatItemKind.System,
                    _ => ChatItemKind.Text }, attachments));
        }
        return result;
        });
    }

    private static string RestoredAttachmentState(string? transferId, string state, IReadOnlyDictionary<string, TransferItem> transfers)
    {
        if (transfers.TryGetValue(transferId ?? "", out var current)) return current.Status.ToString();
        return state is "Offered" or "Queued" or "Transferring" or "Paused" or "RemotePaused" or "Resuming" or "Verifying" or "Committing"
            ? "Failed" : state;
    }

    private async Task<List<TransferItem>> LoadStoredTransfersAsync(string? peerId) =>
        _recovery.MergeHistory((await _database.LoadTransfersAsync(peerId)).Select(value => new TransferItem
        {
            Id = Guid.ParseExact(value.TransferId, "N"), Name = value.FileName, TotalBytes = value.TotalBytes,
            Outgoing = value.Direction.Equals("Outgoing", StringComparison.OrdinalIgnoreCase),
            CompletedBytes = value.CompletedBytes,
            Status = Enum.TryParse<TransferStatus>(value.Status, true, out var status) ? status : TransferStatus.Failed,
            MessageId = value.MessageId is null ? null : Guid.ParseExact(value.MessageId, "N"),
            MimeType = value.MimeType, LocalPath = value.LocalPath, FailureDetail = value.FailureDetail,
            SourceSha256 = value.Sha256 is { } hash ? Convert.ToHexString(hash) : null,
            PeerId = value.PeerId, CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAt),
            PeerName = _storedPeers.GetValueOrDefault(value.PeerId)?.DisplayName ?? Localization.Strings.Get("对端")
        }), peerId).Concat(AllTransfers.Where(x => peerId is null || string.Equals(peerId, x.PeerId, StringComparison.OrdinalIgnoreCase)))
        .DistinctBy(x => x.Id).Select(value =>
        {
            // A retry retains its transfer ID across sessions. The global projection holds
            // the latest attempt; an older session cache may still contain its failure.
            var live = AllTransfers.FirstOrDefault(item => item.Id == value.Id);
            if (live is not null && string.Equals(live.PeerId, value.PeerId, StringComparison.OrdinalIgnoreCase)) return live;
            if (value.IsActive)
            {
                value.Status = TransferStatus.Failed;
                value.FailureDetail = "设备通道已断开，请由发送方重试传输";
            }
            return value;
        }).ToList();

    private void RefreshConversations()
    {
        var active = Sessions.Where(value => value.PeerId is not null && value.Phase == ConnectionPhase.Connected)
            .ToDictionary(value => value.PeerId!, StringComparer.OrdinalIgnoreCase);
        var canonicalPeers = _storedPeers.Values.Where(peer => peer.TrustState != StoredTrustState.Removed)
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
            var preference = PeerPreferences.Get(peer.PeerId);
            return new ConversationSummary(peer.PeerId, string.IsNullOrWhiteSpace(peer.DisplayName) ? Localization.Strings.Get("已信任设备") : peer.DisplayName,
                Enum.TryParse<PeerPlatform>(peer.Platform, true, out var platform) ? platform : PeerPlatform.Unknown,
                availability, session?.SessionId, peer.TransportAddress, conversation?.UnreadCount ?? 0,
                DateTimeOffset.FromUnixTimeMilliseconds(conversation?.LastActivityAt ?? peer.LastSeenAt),
                peer.LastConnectedAt is { } connectedAt ? DateTimeOffset.FromUnixTimeMilliseconds(connectedAt) : null)
                { UsbReady = UsbSessionPolicy.IsReady(Settings.UsbEnabled, peer.PeerId, Sessions), LocalNote = preference.Note, IsPinned = preference.Pinned };
        }).OrderBy(value => value.IsConnected ? 0 : 1).ThenByDescending(value => value.IsPinned).ThenByDescending(value => value.LastActivityAt).ToList();
        Conversations.Clear(); foreach (var value in values) Conversations.Add(value);
        TrustedDevices.Clear();
        foreach (var trusted in _identity.TrustedIdentities)
        {
            var conversation = values.FirstOrDefault(value =>
                value.PeerId.Equals(trusted.PeerIdHex, StringComparison.OrdinalIgnoreCase));
            TrustedDevices.Add(conversation ?? new ConversationSummary(trusted.PeerIdHex,
                Localization.Strings.Get("已信任设备"), PeerPlatform.Unknown, DeviceAvailability.Offline, null, "",
                0, DateTimeOffset.MinValue, null));
        }
        Raise(nameof(HasTrustedDevices)); Raise(nameof(TrustedDeviceCountText));
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
        Raise(nameof(HasWorkspaceUnread)); Raise(nameof(WorkspaceUnreadText));
        Raise(nameof(ActiveUsbReady)); RaiseUsbNotice();
    }

    private void RefreshNearbyNewDevices()
    {
        var knownAddresses = Conversations
            .Select(peer => NormalizeAddress(peer.TransportAddress))
            .Where(address => address.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var address in Sessions.Where(session => session.Phase == ConnectionPhase.Connected)
                     .Select(session => NormalizeAddress(session.TransportAddress)))
            knownAddresses.Add(address);
        var knownPeerIds = Conversations.Select(peer => peer.PeerId).Concat(Sessions.Where(session => session.PeerId is not null && session.Phase == ConnectionPhase.Connected)
            .Select(session => session.PeerId!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectedAddress = SelectedDevice is null ? null : NormalizeAddress(SelectedDevice.Address);
        NearbyNewDevices.Clear();
        foreach (var device in Devices.Where(device =>
                     !knownAddresses.Contains(NormalizeAddress(device.Address)) &&
                     !knownPeerIds.Any(peerId => MatchesPeer(device, peerId))))
            NearbyNewDevices.Add(ProjectNearbyDevice(device));
        SelectedDevice = selectedAddress is null
            ? null
            : NearbyNewDevices.FirstOrDefault(device => NormalizeAddress(device.Address) == selectedAddress);
        RefreshCompletedScanFeedback();
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _connectedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _manuallyDisconnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _notifiedConnections = [];
    private readonly HashSet<Guid> _notifiedTransfers = [];
    public event Action<string, string>? SystemNotificationRequested;

    private async Task TryAutoConnectAsync(IReadOnlyList<NearbyDevice> devices)
    {
        if ((!Settings.AutoConnectTrustedDevices && !Settings.ReconnectAfterDisconnect) || _isDialing || !_sessions.CanAccept) return;
        var now = DateTimeOffset.UtcNow;
        var connectedPeers = Sessions.Where(value => value.PeerId is not null)
            .Select(value => value.PeerId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeAddresses = Sessions.Select(value => value.TransportAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trustedPeerIds = _identity.TrustedIdentities.Select(value => value.PeerIdHex)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _storedPeers.Values.Where(peer => peer.TrustState == StoredTrustState.Trusted &&
                AutoConnectionPolicy.ShouldConnect(trustedPeerIds.Contains(peer.PeerId), _connectedThisRun.ContainsKey(peer.PeerId),
                    _manuallyDisconnected.ContainsKey(peer.PeerId), Settings.AutoConnectTrustedDevices, Settings.ReconnectAfterDisconnect) &&
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
                if ((!Settings.AutoConnectTrustedDevices && !Settings.ReconnectAfterDisconnect) || Devices.Count == 0) continue;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) continue;
                await dispatcher.InvokeAsync(() => TryAutoConnectAsync(Devices.ToArray())).Task.Unwrap();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task MarkConversationReadAsync(string peerId)
    {
        Notifications.Read(peerId);
        await _conversationPersistenceGate.WaitAsync();
        try
        {
        if (!_storedConversations.TryGetValue(peerId, out var conversation) || conversation.UnreadCount == 0) return;
        conversation = conversation with { UnreadCount = 0 };
        await _database.UpsertConversationMetadataAsync(conversation);
        _storedConversations[peerId] = conversation;
        await Application.Current.Dispatcher.InvokeAsync(RefreshConversations);
        }
        finally { _conversationPersistenceGate.Release(); }
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
        Raise(nameof(DraftText)); Raise(nameof(CanEditDraft));
        Raise(nameof(HasActiveConversation));
        Raise(nameof(ShowConversationPlaceholder));
        Raise(nameof(IsOfflineConversation));
        Raise(nameof(ActivePeerTitle));
        Raise(nameof(ActivePeerSubtitle));
        Raise(nameof(ComposerPlaceholder));
        Raise(nameof(ComposerHint));
        Raise(nameof(ShowOfflineHistoryNotice));
        Raise(nameof(ActiveUsbReady)); RaiseUsbNotice();
    }

    private void SetState(ConnectionPhase phase, string status, string detail)
    {
        Phase = phase; Status = status; Detail = detail;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _draftDelay?.Cancel();
        await FlushDraftsAsync();
        _draftDelay?.Dispose();
        _lifetime.Cancel();
        StopObservingHomeTransfers();
        Updates.Dispose();
        await _sessions.DisposeAsync();
        _sessions.UsbEnabled = false;
        StopObservingBluetooth();
        if (_reconnectLoop is not null)
            try { await _reconnectLoop; } catch (OperationCanceledException) { }
        _bluetooth.DevicesChanged -= OnDevicesChanged;
        _bluetooth.Dispose();
        _lifetime.Dispose();
    }
}
