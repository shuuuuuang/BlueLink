using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BlueLink.Domain;
using BlueLink.Protocol;
using Windows.Devices.Radios;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private readonly HashSet<TransferItem> _homeTransfers = [];
    private string _deviceQuery = "";
    private string _fileSearchQuery = "";
    public string FileSearchQuery { get => _fileSearchQuery; internal set => Set(ref _fileSearchQuery, value); }
    private bool _connectedExpanded = true;
    private bool _offlineExpanded = true;
    private bool _nearbyExpanded = true;
    private bool _showFiles;
    private bool _filesAllDevices;
    private bool? _bluetoothEnabled;
    private bool _bluetoothRadioPresent;
    private IReadOnlyList<Radio> _radios = [];
    private bool _observingBluetooth;
    private bool _refreshingBluetooth;
    private readonly Dictionary<string, (ConnectionPhase Phase, string Detail)> _connectionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanCompleted;
    private bool _scanFailed;

    public ListCollectionView ConnectedDevicesView { get; private set; } = null!;
    public ListCollectionView OfflineDevicesView { get; private set; } = null!;
    public ListCollectionView NearbyDevicesView { get; private set; } = null!;
    public string DeviceQuery
    {
        get => _deviceQuery;
        set
        {
            if (!Set(ref _deviceQuery, value)) return;
            ConnectedDevicesView.Refresh();
            OfflineDevicesView.Refresh();
            NearbyDevicesView.Refresh();
            RaiseDeviceGroups();
        }
    }
    public bool ConnectedExpanded { get => _connectedExpanded; set => Set(ref _connectedExpanded, value); }
    public bool OfflineExpanded { get => _offlineExpanded; set => Set(ref _offlineExpanded, value); }
    public bool NearbyExpanded { get => _nearbyExpanded; set => Set(ref _nearbyExpanded, value); }
    public string ConnectedHeading => Localization.Strings.Format($"已连接 ({ConnectedDevicesView.Count})");
    public string OfflineHeading => Localization.Strings.Format($"离线 ({OfflineDevicesView.Count})");
    public string NearbyHeading => IsScanning ? Localization.Strings.Get("附近新设备") : Localization.Strings.Format($"附近新设备 ({NearbyDevicesView.Count})");
    public string ConnectedEmptyText => HasDeviceQuery ? Localization.Strings.Get("没有匹配的已连接设备") : Localization.Strings.Get("暂无已连接设备");
    public string OfflineEmptyText => HasDeviceQuery ? Localization.Strings.Get("没有匹配的离线设备") : Localization.Strings.Get("暂无离线设备");
    public string NearbyEmptyText => HasDeviceQuery ? Localization.Strings.Get("没有匹配的附近设备") : Localization.Strings.Get("暂未发现附近新设备");
    private bool HasDeviceQuery => !string.IsNullOrWhiteSpace(DeviceQuery);
    public bool ShowDeviceSearchEmpty => HasDeviceQuery && ConnectedDevicesView.IsEmpty &&
        OfflineDevicesView.IsEmpty && NearbyDevicesView.IsEmpty;
    public bool ShowDeviceGroups => !ShowDeviceSearchEmpty;

    public bool ShowFiles { get => _showFiles; set { if (Set(ref _showFiles, value)) RaiseWorkspace(); } }
    public bool FilesAllDevices { get => _filesAllDevices; set { if (Set(ref _filesAllDevices, value)) RaiseWorkspace(); } }
    public bool ShowMessageSurface => HasActiveConversation && !ShowFiles;
    public bool ShowConversationPlaceholder => !HasActiveConversation && !ShowFiles;
    public string WorkspaceTitle => ShowFiles && (FilesAllDevices || !HasActiveConversation) ? Localization.Strings.Get("文件管理") : ActivePeerTitle;
    public bool ShowPeerIdentity => HasActiveConversation && !(ShowFiles && FilesAllDevices);
    public string? HighlightedPeerId => ShowFiles && FilesAllDevices ? null : _activePeerId;
    public string? ActivePeerId => _activePeerId;
    public ConversationSummary? ActiveConnectedConversation => ConnectedConversations.FirstOrDefault(peer =>
        string.Equals(peer.PeerId, _activePeerId, StringComparison.OrdinalIgnoreCase));
    public ConversationSummary? ActiveOfflineConversation => OfflineConversations.FirstOrDefault(peer =>
        string.Equals(peer.PeerId, _activePeerId, StringComparison.OrdinalIgnoreCase));
    private int WorkspaceUnreadCount => ShowFiles && FilesAllDevices
        ? Conversations.Sum(peer => peer.UnreadCount)
        : Conversations.FirstOrDefault(peer => peer.PeerId == _activePeerId)?.UnreadCount ?? 0;
    public bool HasWorkspaceUnread => WorkspaceUnreadCount > 0;
    public string WorkspaceUnreadText => WorkspaceUnreadCount > 99 ? "99+" : WorkspaceUnreadCount.ToString();
    public bool IsConversationEmpty => HasActiveConversation && Messages.Count == 0;
    public string EmptyConversationHint => Localization.Strings.Format($"发送第一条消息，开始与 {ActivePeerTitle} 会话。");
    public string ActivePeerIcon => "/BlueLink;component/Assets/Figma/" +
        (Conversations.FirstOrDefault(peer => peer.PeerId == _activePeerId)?.Platform switch
        { PeerPlatform.Android => "phone.png", PeerPlatform.Windows => "desktop.png", _ => "generic.png" });

    public bool? BluetoothEnabled => _bluetoothEnabled;
    public bool IsBluetoothUnavailable => BluetoothEnabled == false;
    public bool ShowBluetoothBadgeNotice => IsBluetoothUnavailable && ActiveSessionCount == 0;
    public string ConnectionBadgeText => ShowBluetoothBadgeNotice ? Localization.Strings.Get("蓝牙未开启") : ActiveSessionCountText;
    public string BluetoothNoticeTitle => _bluetoothRadioPresent ? Localization.Strings.Get("蓝牙未开启") : Localization.Strings.Get("未检测到蓝牙");
    public bool ShowOfflineHistoryNotice => IsOfflineConversation && !IsBluetoothUnavailable && !IsConnecting;
    public bool ShowOfflineSend => !IsConnected && !IsBluetoothUnavailable;
    public string ComposerHint => IsConnected || !IsBluetoothUnavailable
        ? Localization.Strings.Get(ComposerShortcuts.HintKey(Settings.SendShortcut))
        : Localization.Strings.Get("打开蓝牙后即可继续发送");
    public bool HasConnectedDevices => ActiveSessionCount > 0;

    private void UpdateConnectionAttempt(string address, ConnectionPhase phase, string detail)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        _scanCompleted = false;
        _scanFailed = false;
        ScanFeedback = "";
        _connectionAttempts[NormalizeAddress(address)] = (phase, detail);
        RefreshNearbyNewDevices();
    }

    private NearbyDevice ProjectNearbyDevice(NearbyDevice device) =>
        _connectionAttempts.TryGetValue(NormalizeAddress(device.Address), out var attempt)
            ? device with { AttemptPhase = attempt.Phase, AttemptDetail = attempt.Detail }
            : device;

    private void InitializeHomeViews()
    {
        AllTransfers.CollectionChanged += HomeTransfersChanged;
        ConnectedDevicesView = new ListCollectionView(ConnectedConversations);
        OfflineDevicesView = new ListCollectionView(OfflineConversations);
        NearbyDevicesView = new ListCollectionView(NearbyNewDevices);
        ConnectedDevicesView.Filter = MatchesDeviceQuery;
        OfflineDevicesView.Filter = MatchesDeviceQuery;
        NearbyDevicesView.Filter = MatchesDeviceQuery;
        ((INotifyCollectionChanged)ConnectedDevicesView).CollectionChanged += HomeDevicesChanged;
        ((INotifyCollectionChanged)OfflineDevicesView).CollectionChanged += HomeDevicesChanged;
        ((INotifyCollectionChanged)NearbyDevicesView).CollectionChanged += HomeDevicesChanged;
        Messages.CollectionChanged += (_, _) => Raise(nameof(IsConversationEmpty));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasActiveConversation) or nameof(ActivePeerTitle)) RaiseWorkspace();
            if (e.PropertyName == nameof(ActiveSessionCountText)) { Raise(nameof(ConnectionBadgeText)); Raise(nameof(ShowBluetoothBadgeNotice)); Raise(nameof(HasConnectedDevices)); }
            if (e.PropertyName is nameof(IsConnected) or nameof(IsBluetoothUnavailable)) Raise(nameof(ShowOfflineSend));
        };
    }

    private bool MatchesDeviceQuery(object value)
    {
        var query = DeviceQuery.Trim();
        return value switch
        {
            ConversationSummary peer => HomeDeviceSearch.Matches(query, peer.PeerName + " " + peer.LocalNote, peer.PlatformText, peer.TransportAddress),
            NearbyDevice device => HomeDeviceSearch.Matches(query, device.Name, device.Platform.ToString(), device.Address),
            _ => false
        };
    }

    private void HomeDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseConversationSelection();
        RaiseDeviceGroups();
        RefreshDeviceTransferIndicators();
    }

    private void HomeTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var previous in _homeTransfers.Where(transfer => !AllTransfers.Contains(transfer)).ToArray())
        {
            previous.PropertyChanged -= HomeTransferChanged;
            _homeTransfers.Remove(previous);
        }
        foreach (var transfer in AllTransfers)
            if (_homeTransfers.Add(transfer)) transfer.PropertyChanged += HomeTransferChanged;
        RefreshDeviceTransferIndicators();
    }

    private void HomeTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferItem.Status)) return;
        if (Application.Current.Dispatcher.CheckAccess()) RefreshDeviceTransferIndicators();
        else Application.Current.Dispatcher.BeginInvoke(new Action(RefreshDeviceTransferIndicators));
    }

    private void RefreshDeviceTransferIndicators()
    {
        if (_disposeStarted != 0) return;
        foreach (var peer in ConnectedConversations.Concat(OfflineConversations))
        {
            var active = peer.IsConnected ? AllTransfers
                .Where(transfer => string.Equals(transfer.PeerId, peer.PeerId, StringComparison.OrdinalIgnoreCase) &&
                    transfer.IsActive && transfer.Role != AttachmentRole.ImagePreview)
                .OrderBy(transfer => transfer.CreatedAt).ThenBy(transfer => transfer.Id).ToArray() : [];
            peer.ActiveTransferCount = active.Length;
            peer.ActiveTransfer = active.FirstOrDefault();
        }
    }

    private void StopObservingHomeTransfers()
    {
        AllTransfers.CollectionChanged -= HomeTransfersChanged;
        foreach (var transfer in _homeTransfers) transfer.PropertyChanged -= HomeTransferChanged;
        _homeTransfers.Clear();
    }
    private void RaiseDeviceGroups()
    {
        Raise(nameof(ConnectedHeading)); Raise(nameof(OfflineHeading)); Raise(nameof(NearbyHeading));
        Raise(nameof(ConnectedEmptyText)); Raise(nameof(OfflineEmptyText)); Raise(nameof(NearbyEmptyText));
        Raise(nameof(ShowDeviceSearchEmpty)); Raise(nameof(ShowDeviceGroups));
    }
    private void RaiseWorkspace()
    {
        RaiseConversationSelection();
        Raise(nameof(ShowMessageSurface)); Raise(nameof(ShowConversationPlaceholder));
        Raise(nameof(WorkspaceTitle));
        Raise(nameof(ActiveUsbReady)); RaiseUsbNotice(); Raise(nameof(ShowPeerIdentity));
        Raise(nameof(HighlightedPeerId));
        Raise(nameof(ActivePeerId)); Raise(nameof(DraftText)); Raise(nameof(CanEditDraft));
        Raise(nameof(ActivePeerIcon)); Raise(nameof(IsConversationEmpty));
        Raise(nameof(EmptyConversationHint));
        Raise(nameof(HasWorkspaceUnread)); Raise(nameof(WorkspaceUnreadText));
    }

    private void RaiseConversationSelection()
    {
        Raise(nameof(ActiveConnectedConversation));
        Raise(nameof(ActiveOfflineConversation));
    }

    private async Task ObserveBluetoothAsync()
    {
        _observingBluetooth = true;
        await RefreshBluetoothStatusAsync();
    }

    public async Task RefreshBluetoothStatusAsync()
    {
        if (!_observingBluetooth || _refreshingBluetooth || _disposeStarted != 0) return;
        _refreshingBluetooth = true;
        try
        {
            var radios = (await Radio.GetRadiosAsync()).Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
            foreach (var radio in _radios) radio.StateChanged -= BluetoothRadioChanged;
            _radios = radios;
            foreach (var radio in _radios) radio.StateChanged += BluetoothRadioChanged;
            UpdateBluetoothStatus();
        }
        catch
        {
            // A failed status query is unknown, never evidence that the radio is off.
            _bluetoothEnabled = null;
            RaiseBluetoothStatus();
        }
        finally { _refreshingBluetooth = false; }
    }

    private void BluetoothRadioChanged(Radio sender, object args) =>
        Application.Current?.Dispatcher.BeginInvoke(UpdateBluetoothStatus);

    private void UpdateBluetoothStatus()
    {
        if (_disposeStarted != 0) return;
        _bluetoothRadioPresent = _radios.Count > 0;
        _bluetoothEnabled = _radios.Any(radio => radio.State == RadioState.On);
        if (_bluetoothEnabled == false)
        {
            _dialCancellation?.Cancel();
            _connectionAttempts.Clear();
            Devices.Clear();
            RefreshConversations();
        }
        RaiseBluetoothStatus();
    }

    private void RaiseBluetoothStatus()
    {
        Raise(nameof(BluetoothEnabled)); Raise(nameof(IsBluetoothUnavailable)); Raise(nameof(CanScan));
        Raise(nameof(ConnectionBadgeText)); Raise(nameof(ShowBluetoothBadgeNotice)); Raise(nameof(BluetoothNoticeTitle)); Raise(nameof(CanConnectSelected));
        Raise(nameof(ActivePeerSubtitle)); Raise(nameof(ComposerPlaceholder)); Raise(nameof(ComposerHint)); Raise(nameof(ShowOfflineHistoryNotice));
    }

    private void StopObservingBluetooth()
    {
        foreach (var radio in _radios) radio.StateChanged -= BluetoothRadioChanged;
        _observingBluetooth = false;
    }
}
