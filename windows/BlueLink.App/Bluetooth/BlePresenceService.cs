using System.Collections.Concurrent;
using BlueLink.Domain;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BlueLink.Bluetooth;

public sealed class BlePresenceService : IDisposable
{
    public static readonly Guid PresenceUuid = Guid.Parse("9c6c51a8-8f9a-4f67-96aa-2df57177b102");
    public static readonly Guid RendezvousUuid = Guid.Parse("9c6c51a8-8f9a-4f67-96aa-2df57177b103");
    public static readonly Guid TransportOfferUuid = Guid.Parse("9c6c51a8-8f9a-4f67-96aa-2df57177b104");
    public static readonly Guid ConnectRequestUuid = Guid.Parse("9c6c51a8-8f9a-4f67-96aa-2df57177b105");
    private const ushort CompanyId = 0xFFFF;
    private static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<ulong, NearbyDevice> _seen = new();
    private readonly ConcurrentDictionary<ulong, byte> _resolving = new();
    private readonly byte[] _presenceId;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private GattServiceProvider? _rendezvous;
    private ulong _localAddress;

    public string? LastError { get; private set; }
    public event Action<IReadOnlyList<NearbyDevice>>? DevicesChanged;

    public BlePresenceService(byte[] identityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(identityPublicKey);
        _presenceId = System.Security.Cryptography.SHA256.HashData(identityPublicKey)[..8];
    }

    public async Task StartAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync()
                ?? throw new IOException("未找到 Windows 蓝牙适配器");
            if (!adapter.IsLowEnergySupported || !adapter.IsCentralRoleSupported)
                throw new IOException("此蓝牙适配器不支持 BLE 扫描");
            _localAddress = adapter.BluetoothAddress;

            // Scanning is independent from peripheral advertising. A publisher or GATT
            // failure must not prevent Windows from discovering an Android peer.
            EnsureWatcherStarted();

            var warnings = new List<string>();
            if (adapter.IsPeripheralRoleSupported)
            {
                try { await EnsureRendezvousStartedAsync(); }
                catch (Exception failure)
                {
                    StopRendezvous();
                    warnings.Add($"GATT Rendezvous 不可用：{FailureDetail(failure)}");
                }

            }
            else
            {
                warnings.Add("此蓝牙适配器不支持 BLE Peripheral，需使用 Classic 发现回退");
            }

            LastError = warnings.Count == 0 ? null : string.Join("；", warnings);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<IReadOnlyList<NearbyDevice>> ScanAsync(CancellationToken token = default)
    {
        if (_watcher is null) await StartAsync();
        if (_watcher is null) throw new IOException("BLE 扫描器未初始化");
        if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Start();
        PublishSnapshot();
        await Task.Delay(TimeSpan.FromSeconds(12), token);
        var result = Snapshot();
        DevicesChanged?.Invoke(result);
        return result;
    }

    private void EnsureWatcherStarted()
    {
        if (_watcher is not null)
        {
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Start();
            return;
        }

        // Presence discovery needs every advertisement payload. RSSI sampling can
        // collapse different packets emitted by the same phone address and discard
        // the packet that contains BlueLink manufacturer data.
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += WatcherOnReceived;
        watcher.Stopped += WatcherOnStopped;
        try
        {
            watcher.Start();
            _watcher = watcher;
        }
        catch
        {
            watcher.Received -= WatcherOnReceived;
            watcher.Stopped -= WatcherOnStopped;
            throw;
        }
    }

    private async Task EnsureRendezvousStartedAsync()
    {
        if (_rendezvous is not null) return;
        var result = await GattServiceProvider.CreateAsync(RendezvousUuid);
        if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
            throw new IOException($"创建服务失败：{result.Error}");

        var provider = result.ServiceProvider;
        try
        {
            var characteristic = await provider.Service.CreateCharacteristicAsync(TransportOfferUuid,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read,
                    ReadProtectionLevel = GattProtectionLevel.Plain,
                    StaticValue = Buffer(TransportOffer(_localAddress)),
                    UserDescription = "BlueLink RFCOMM transport offer",
                });
            if (characteristic.Error != BluetoothError.Success)
                throw new IOException($"创建 Transport Offer 特征失败：{characteristic.Error}");
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
                ServiceData = Buffer(RendezvousPresencePayload(_presenceId)),
            });
            _rendezvous = provider;
        }
        catch
        {
            try { provider.StopAdvertising(); } catch { }
            throw;
        }
    }

    public IReadOnlyList<NearbyDevice> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - ResultTtl;
        foreach (var stale in _seen.Where(item => item.Value.LastSeen < cutoff).Select(item => item.Key))
            _seen.TryRemove(stale, out _);
        return _seen.Values
            .GroupBy(item => string.IsNullOrWhiteSpace(item.DiscoveryId) ? item.Address : item.DiscoveryId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.CanInitiate)
                .ThenByDescending(item => item.LastSeen).First())
            .OrderByDescending(item => item.Rssi ?? short.MinValue).ThenBy(item => item.Name).ToList();
    }

    private void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.BluetoothAddress == _localAddress) return;
        var manufacturer = args.Advertisement.GetManufacturerDataByCompanyId(CompanyId).FirstOrDefault();
        var address = args.BluetoothAddress;
        if (manufacturer is not null)
        {
            var payload = Bytes(manufacturer.Data);
            if (payload.Length >= 12 && payload[0] == 0x42 && payload[1] == 0x4c && payload[2] == 1 &&
                !payload.AsSpan(4, 8).SequenceEqual(_presenceId))
            {
                var platform = payload[3] switch { 1 => PeerPlatform.Android, 2 => PeerPlatform.Windows, _ => PeerPlatform.Unknown };
                var presenceId = Convert.ToHexString(payload, 4, 8);
                StorePresence(args, platform, presenceId, canInitiate: false);
                return;
            }
        }

        if (TryReadRendezvousPresence(args.Advertisement, out var rendezvousPlatform, out var rendezvousId))
        {
            if (rendezvousPlatform == PeerPlatform.Windows && rendezvousId == Convert.ToHexString(_presenceId, 0, 6)) return;
            var connectable = args.AdvertisementType == BluetoothLEAdvertisementType.ConnectableUndirected;
            StorePresence(args, rendezvousPlatform, rendezvousId,
                canInitiate: rendezvousPlatform == PeerPlatform.Android && connectable);
            if (rendezvousPlatform == PeerPlatform.Android && connectable &&
                _seen.TryGetValue(address, out var stored) &&
                stored.Name.StartsWith("BlueLink ", StringComparison.Ordinal))
                _ = ResolvePeerInfoAsync(address);
            return;
        }

        // Active scanning delivers the scan response (which carries the friendly
        // Bluetooth name) as a separate event. Merge it with the primary packet.
        if (!string.IsNullOrWhiteSpace(args.Advertisement.LocalName) && _seen.TryGetValue(address, out var current))
        {
            _seen[address] = current with
            {
                Name = args.Advertisement.LocalName,
                Rssi = args.RawSignalStrengthInDBm,
                LastSeen = args.Timestamp,
            };
            PublishSnapshot();
        }
    }

    private void StorePresence(BluetoothLEAdvertisementReceivedEventArgs args, PeerPlatform platform,
        string presenceId, bool canInitiate)
    {
        var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName)
            ? $"BlueLink {platform} {presenceId[^4..]}"
            : args.Advertisement.LocalName;
        if (_seen.TryGetValue(args.BluetoothAddress, out var existing) &&
            !existing.Name.StartsWith("BlueLink ", StringComparison.Ordinal)) name = existing.Name;
        _seen[args.BluetoothAddress] = new NearbyDevice(
            $"ble:{args.BluetoothAddress:X12}", name, FormatAddress(args.BluetoothAddress), platform,
            args.RawSignalStrengthInDBm, args.Timestamp, canInitiate || existing?.CanInitiate == true,
            presenceId);
        PublishSnapshot();
    }

    private async Task ResolvePeerInfoAsync(ulong address)
    {
        if (!_resolving.TryAdd(address, 0)) return;
        try
        {
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null) return;
            var services = await device.GetGattServicesForUuidAsync(RendezvousUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success) return;
            foreach (var service in services.Services)
            {
                using (service)
                {
                    var offerResult = await service.GetCharacteristicsForUuidAsync(TransportOfferUuid, BluetoothCacheMode.Uncached);
                    var requestResult = await service.GetCharacteristicsForUuidAsync(ConnectRequestUuid, BluetoothCacheMode.Uncached);
                    var offer = offerResult.Characteristics.FirstOrDefault();
                    if (offer is null) continue;
                    var read = await offer.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (read.Status != GattCommunicationStatus.Success ||
                        !TryParseTransportOffer(Bytes(read.Value), requireClassicAddress: false, out _, out var peerName)) continue;
                    if (_seen.TryGetValue(address, out var current))
                    {
                        _seen[address] = current with
                        {
                            Name = string.IsNullOrWhiteSpace(peerName) ? current.Name : peerName,
                            CanInitiate = requestResult.Status == GattCommunicationStatus.Success &&
                                requestResult.Characteristics.Count > 0,
                            LastSeen = DateTimeOffset.UtcNow,
                        };
                        PublishSnapshot();
                    }
                    return;
                }
            }
        }
        catch { /* A later advertisement retries metadata resolution. */ }
        finally { _resolving.TryRemove(address, out _); }
    }

    public async Task RequestConnectionAsync(NearbyDevice device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var address = ParseAddress(device.Address);
        using var peer = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new IOException("无法打开 Android GATT 设备");
        var services = await peer.GetGattServicesForUuidAsync(RendezvousUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success)
            throw new IOException($"无法发现 Android Rendezvous：{services.Status}");
        foreach (var service in services.Services)
        {
            using (service)
            {
                var characteristics = await service.GetCharacteristicsForUuidAsync(ConnectRequestUuid,
                    BluetoothCacheMode.Uncached);
                var request = characteristics.Characteristics.FirstOrDefault();
                if (request is null) continue;
                token.ThrowIfCancellationRequested();
                var result = await request.WriteValueWithResultAsync(Buffer(TransportOffer(_localAddress)),
                    GattWriteOption.WriteWithResponse);
                if (result.Status != GattCommunicationStatus.Success)
                    throw new IOException($"Android 拒绝回连请求：{result.Status}");
                return;
            }
        }
        throw new IOException("Android 未公开 BlueLink Connect Request 特征");
    }

    private void WatcherOnStopped(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success) LastError = $"BLE 扫描停止：{args.Error}";
    }

    private static byte[] RendezvousPresencePayload(byte[] presenceId)
    {
        var payload = new byte[8];
        payload[0] = 1;
        payload[1] = 2;
        presenceId.AsSpan(0, 6).CopyTo(payload.AsSpan(2));
        return payload;
    }

    private static byte[] TransportOffer(ulong classicAddress)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName);
        if (name.Length > 80) name = name[..80];
        var payload = new byte[8 + name.Length];
        payload[0] = 1;
        for (var index = 0; index < 6; index++) payload[1 + index] = (byte)(classicAddress >> ((5 - index) * 8));
        payload[7] = (byte)name.Length;
        name.CopyTo(payload, 8);
        return payload;
    }

    private static bool TryReadRendezvousPresence(BluetoothLEAdvertisement advertisement,
        out PeerPlatform platform, out string presenceId)
    {
        platform = PeerPlatform.Unknown;
        presenceId = "";
        var canonical = Convert.FromHexString("9C6C51A88F9A4F6796AA2DF57177B103");
        var wire = canonical.Reverse().ToArray();
        var dotnet = RendezvousUuid.ToByteArray();
        foreach (var section in advertisement.GetSectionsByType(0x21))
        {
            var value = Bytes(section.Data);
            if (TryParseRendezvousSection(value, canonical, wire, dotnet, out platform, out presenceId)) return true;
        }
        return false;
    }

    private static bool TryParseRendezvousSection(byte[] value, byte[] canonical, byte[] wire, byte[] dotnet,
        out PeerPlatform platform, out string presenceId)
    {
        platform = PeerPlatform.Unknown;
        presenceId = "";
        if (value.Length < 24) return false;
        var uuid = value.AsSpan(0, 16);
        if (!uuid.SequenceEqual(canonical) && !uuid.SequenceEqual(wire) && !uuid.SequenceEqual(dotnet)) return false;
        if (value[16] != 1) return false;
        platform = value[17] switch { 1 => PeerPlatform.Android, 2 => PeerPlatform.Windows, _ => PeerPlatform.Unknown };
        presenceId = Convert.ToHexString(value, 18, 6);
        return true;
    }

    private static bool TryParseTransportOffer(byte[] value, bool requireClassicAddress,
        out ulong classicAddress, out string name)
    {
        classicAddress = 0;
        name = "";
        if (value.Length < 8 || value[0] != 1) return false;
        for (var index = 0; index < 6; index++) classicAddress = (classicAddress << 8) | value[1 + index];
        if (requireClassicAddress && classicAddress == 0) return false;
        var length = value[7];
        if (value.Length < 8 + length) return false;
        name = length == 0 ? "" : System.Text.Encoding.UTF8.GetString(value, 8, length).Trim();
        return true;
    }

    private static ulong ParseAddress(string address)
    {
        var hex = address.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException("BLE 地址格式无效");
    }

    private static IBuffer Buffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string FormatAddress(ulong value) => string.Join(":",
        Enumerable.Range(0, 6).Select(index => $"{(value >> ((5 - index) * 8)) & 0xff:X2}"));

    private static string FailureDetail(Exception failure) =>
        $"{failure.Message} (0x{failure.HResult:X8})";

    private void PublishSnapshot()
    {
        var handler = DevicesChanged;
        if (handler is not null) handler(Snapshot());
    }

    private void StopRendezvous()
    {
        if (_rendezvous is null) return;
        try { _rendezvous.StopAdvertising(); } catch { }
        _rendezvous = null;
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Received -= WatcherOnReceived;
            _watcher.Stopped -= WatcherOnStopped;
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
        }
        StopRendezvous();
        _watcher = null;
        _seen.Clear();
        _resolving.Clear();
        _startGate.Dispose();
    }
}
