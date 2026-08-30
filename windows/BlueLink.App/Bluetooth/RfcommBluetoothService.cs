using BlueLink.Domain;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace BlueLink.Bluetooth;

public sealed record RfcommConnection(StreamSocket Socket, Stream Input, Stream Output, string PeerName,
    bool ListenerRole) : IAsyncDisposable
{
    public string TransportAddress => Socket.Information.RemoteAddress?.DisplayName ?? string.Empty;

    public async ValueTask DisposeAsync()
    {
        await Input.DisposeAsync();
        await Output.DisposeAsync();
        Socket.Dispose();
    }
}

public sealed class RfcommBluetoothService : IDisposable
{
    public static readonly Guid ServiceUuid = Guid.Parse("9c6c51a8-8f9a-4f67-96aa-2df57177b101");
    private readonly RfcommServiceId _serviceId = RfcommServiceId.FromUuid(ServiceUuid);
    private StreamSocketListener? _listener;
    private RfcommServiceProvider? _provider;
    private readonly BlePresenceService _presence;
    private TaskCompletionSource<RfcommConnection>? _pendingCallback;
    private string? _rfcommError;
    public event Func<RfcommConnection, Task>? ConnectionAccepted;
    public event Action<IReadOnlyList<NearbyDevice>>? DevicesChanged
    {
        add => _presence.DevicesChanged += value;
        remove => _presence.DevicesChanged -= value;
    }
    public string? StartupWarning => string.Join("；",
        new[] { _rfcommError, _presence.LastError }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public RfcommBluetoothService(byte[] identityPublicKey)
    {
        _presence = new BlePresenceService(identityPublicKey);
    }

    public async Task StartAsync()
    {
        if (_listener is null)
        {
            RfcommServiceProvider? provider = null;
            StreamSocketListener? listener = null;
            try
            {
                provider = await RfcommServiceProvider.CreateAsync(_serviceId);
                listener = new StreamSocketListener();
                listener.ConnectionReceived += ListenerOnConnectionReceived;
                await listener.BindServiceNameAsync(provider.ServiceId.AsString(),
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                provider.StartAdvertising(listener, true);
                _provider = provider;
                _listener = listener;
                _rfcommError = null;
            }
            catch (Exception failure)
            {
                if (listener is not null)
                {
                    listener.ConnectionReceived -= ListenerOnConnectionReceived;
                    listener.Dispose();
                }
                try { if (provider is not null) provider.StopAdvertising(); } catch { }
                _rfcommError = $"RFCOMM 监听不可用：{failure.Message} (0x{failure.HResult:X8})";
            }
        }

        // Presence scanning remains useful even when the RFCOMM listener cannot bind.
        await _presence.StartAsync();
    }

    public async Task<IReadOnlyList<NearbyDevice>> ScanAsync()
    {
        return await _presence.ScanAsync();
    }

    public async Task<RfcommConnection> ConnectAsync(NearbyDevice device, CancellationToken token)
    {
        if (!device.CanInitiate)
            throw new InvalidOperationException("该设备未公开可用的 BlueLink 连接请求服务");
        if (device.Platform == PeerPlatform.Android)
        {
            var completion = new TaskCompletionSource<RfcommConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _pendingCallback, completion, null) is not null)
                throw new InvalidOperationException("已有 Android 回连请求正在等待处理");
            try
            {
                await _presence.RequestConnectionAsync(device, token);
                var connection = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                return connection with { PeerName = device.Name };
            }
            catch
            {
                Interlocked.CompareExchange(ref _pendingCallback, null, completion);
                throw;
            }
            finally { Interlocked.CompareExchange(ref _pendingCallback, null, completion); }
        }
        var service = await RfcommDeviceService.FromIdAsync(device.Id)
            ?? throw new IOException("设备未公开 BlueLink RFCOMM 服务，请先在对端打开蓝联");
        var socket = new StreamSocket();
        using var registration = token.Register(socket.Dispose);
        await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        return Wrap(socket, device.Name, listenerRole: false);
    }

    private async void ListenerOnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        try
        {
            var connection = Wrap(args.Socket, args.Socket.Information.RemoteAddress?.DisplayName ?? "附近设备",
                listenerRole: true);
            var pending = Interlocked.Exchange(ref _pendingCallback, null);
            if (pending is not null) pending.TrySetResult(connection);
            else if (ConnectionAccepted is { } handler) await handler(connection);
            else args.Socket.Dispose();
        }
        catch { args.Socket.Dispose(); }
    }

    private static RfcommConnection Wrap(StreamSocket socket, string name, bool listenerRole) =>
        new(socket, socket.InputStream.AsStreamForRead(), socket.OutputStream.AsStreamForWrite(), name, listenerRole);

    public void Dispose()
    {
        if (_provider is not null && _listener is not null) _provider.StopAdvertising();
        if (_listener is not null) _listener.ConnectionReceived -= ListenerOnConnectionReceived;
        _listener?.Dispose();
        _listener = null;
        _provider = null;
        Interlocked.Exchange(ref _pendingCallback, null)?.TrySetCanceled();
        _presence.Dispose();
    }
}
