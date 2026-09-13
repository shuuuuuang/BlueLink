using System.ComponentModel;
using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Transport;

namespace BlueLink.Usb;

public interface IUsbHostConnection : IPeerConnection, IUsbControlPipe
{
    Task Closed { get; }
    UsbLinkSpeed Speed { get; }
}
public interface IUsbHostBackend
{
    Task<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken token);
    Task<IUsbHostConnection> OpenAsync(UsbDevice device, CancellationToken token);
}
public sealed class WindowsUsbBackend : IUsbHostBackend
{
    public Task<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken token) => Task.Run(() => new WindowsUsbInventory().Enumerate(token), token);
    public Task<IUsbHostConnection> OpenAsync(UsbDevice device, CancellationToken token) => Task.Run<IUsbHostConnection>(() => new WinUsbConnection(device), token);
}

/// <summary>Each physical device has its own negotiation, lifetime and authenticated peer state.</summary>
public sealed class UsbHost(IUsbHostBackend backend, Func<string> serial, Func<IUsbHostConnection, Task> accept,
    Func<string, bool> hasBluetooth, TimeSpan? pollInterval = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsbSnapshot> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _disposed;
    private volatile bool _enabled;
    public IReadOnlyList<UsbSnapshot> Snapshots { get { lock (_gate) return _states.Values.ToArray(); } }
    public event Action<UsbSnapshot>? Changed;

    public async Task SetEnabledAsync(bool enabled)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled) await StopAsync().ConfigureAwait(false);
            else if (_loop is null || _loop.IsCompleted)
            {
                _stop?.Dispose();
                _stop = new();
                lock (_gate) _states.Clear();
                var token = _stop.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void ObserveSession(SessionSnapshot session)
    {
        List<UsbSnapshot> changes = [];
        lock (_gate)
        {
            foreach (var worker in _workers.Values)
            {
                var state = worker.State;
                if (_enabled && !worker.Stop.IsCancellationRequested && worker.Connection is { } connection &&
                    session.Transport == TransportKind.Usb &&
                    session.TransportAddress.Equals(connection.TransportAddress, StringComparison.OrdinalIgnoreCase))
                {
                    if (session.Phase == ConnectionPhase.Connected && !string.IsNullOrWhiteSpace(session.PeerId))
                        state = state with { PeerId = session.PeerId, PeerName = session.PeerName,
                            Stage = connection.Speed switch { UsbLinkSpeed.FullSpeed => UsbStage.FullSpeed, UsbLinkSpeed.HighSpeed => UsbStage.HighSpeed, _ => UsbStage.Ready },
                            Speed = connection.Speed, BluetoothAvailable = hasBluetooth(session.PeerId), Error = null };
                    else if (session.Phase == ConnectionPhase.TrustRequired)
                        state = state with { Stage = UsbStage.Negotiating, Error = null };
                    else if (session.Phase == ConnectionPhase.Disconnected)
                        state = Disconnected(state);
                }
                else if (state.PeerId is { } peerId && peerId.Equals(session.PeerId, StringComparison.OrdinalIgnoreCase) &&
                    session.Transport == TransportKind.Bluetooth)
                {
                    state = state with { BluetoothAvailable = hasBluetooth(peerId) };
                    if (state.Stage is UsbStage.Fallback or UsbStage.Unavailable) state = Disconnected(state);
                }
                if (!_enabled) state = state with { Stage = UsbStage.Off };
                if (state != worker.State)
                {
                    worker.State = state;
                    _states[state.DeviceId] = state;
                    changes.Add(state);
                }
            }
        }
        foreach (var state in changes) Changed?.Invoke(state);
    }

    private UsbSnapshot Disconnected(UsbSnapshot state)
    {
        var bluetooth = state.PeerId is { } peerId && hasBluetooth(peerId);
        return state with { Stage = !_enabled ? UsbStage.Off : bluetooth ? UsbStage.Fallback : UsbStage.Unavailable,
            BluetoothAvailable = bluetooth, Error = null };
    }

    private async Task RunAsync(CancellationToken token)
    {
        // A failed device is retried only after unplug/replug or an explicit off/on cycle.
        // It must neither spin permission requests nor block another device's negotiation.
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var devices = await backend.EnumerateAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var present = devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    attempted.RemoveWhere(id => !present.Contains(id));
                    DeviceWorker[] removed;
                    lock (_gate) removed = _workers.Values.Where(worker => !present.Contains(worker.Device.InstanceId)).ToArray();
                    foreach (var worker in removed)
                    {
                        worker.Stop.Cancel();
                        await worker.Task.ConfigureAwait(false);
                        lock (_gate) _workers.Remove(worker.Device.InstanceId);
                        worker.Stop.Dispose();
                    }
                    foreach (var device in devices.Where(value => value.AndroidCandidate && !attempted.Contains(value.InstanceId))
                                 .OrderByDescending(value => value.AccessoryMode)
                                 .ThenByDescending(value => value.Driver.Equals("WinUSB", StringComparison.OrdinalIgnoreCase)))
                    {
                        DeviceWorker worker;
                        lock (_gate)
                        {
                            // Match the supported maximum peer count without occupying slots with completed failures.
                            if (_workers.Values.Count(value => !value.Task.IsCompleted) >= 8) break;
                            if (_workers.ContainsKey(device.InstanceId)) continue;
                            attempted.Add(device.InstanceId);
                            worker = new(device, CancellationTokenSource.CreateLinkedTokenSource(token));
                            _workers.Add(device.InstanceId, worker);
                            worker.Task = Task.Run(() => RunDeviceAsync(worker));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception failure)
                {
                    // Enumeration has no authenticated peer identity; never project it onto a random conversation.
                    SessionLog.Write("usb", "enumeration: " + failure.GetType().Name);
                }
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            DeviceWorker[] workers;
            lock (_gate) workers = _workers.Values.ToArray();
            foreach (var worker in workers) worker.Stop.Cancel();
            await Task.WhenAll(workers.Select(worker => worker.Task)).ConfigureAwait(false);
            lock (_gate) _workers.Clear();
            foreach (var worker in workers) worker.Stop.Dispose();
        }
    }

    private async Task RunDeviceAsync(DeviceWorker worker)
    {
        var device = worker.Device;
        var token = worker.Stop.Token;
        IUsbHostConnection? connection = null;
        try
        {
            Publish(worker, worker.State);
            if (device.PolicyBlocked) throw new UsbFailure(UsbStage.PolicyBlocked, "USB 被系统策略阻止");
            if (device.ProblemCode != 0 || !device.Driver.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(device.InterfacePath))
                throw new UsbFailure(UsbStage.DriverMissing, "电脑端 USB 驱动未就绪");
            connection = await backend.OpenAsync(device, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate) worker.Connection = connection;
            if (!device.AccessoryMode)
            {
                await AoaProtocol.StartAccessoryAsync(connection, serial(), token).ConfigureAwait(false);
                // The next enumeration observes the new AOA interface independently of other devices.
            }
            else
            {
                Publish(worker, worker.State with { Stage = UsbStage.Authorization, Speed = connection.Speed });
                await accept(connection).ConfigureAwait(false);
                await connection.Closed.WaitAsync(token).ConfigureAwait(false);
                Publish(worker, Disconnected(worker.State));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(worker, Disconnected(worker.State));
        }
        catch (Exception failure)
        {
            var stage = failure is UsbFailure usb ? usb.Stage : failure is Win32Exception { NativeErrorCode: 5 }
                ? UsbStage.PermissionDenied : UsbStage.Unavailable;
            Publish(worker, worker.State with { Stage = stage, Error = failure is UsbFailure ? null : failure.Message });
        }
        finally
        {
            if (connection is not null)
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) { SessionLog.Write("usb", "close: " + failure.GetType().Name); }
            lock (_gate) worker.Connection = null;
        }
    }

    private void Publish(DeviceWorker worker, UsbSnapshot state)
    {
        lock (_gate)
        {
            if (!_enabled) state = state with { Stage = UsbStage.Off };
            worker.State = state;
            if (_states.GetValueOrDefault(state.DeviceId) == state) return;
            _states[state.DeviceId] = state;
        }
        Changed?.Invoke(state);
    }

    private async Task StopAsync()
    {
        _stop?.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; _loop = null;
        UsbSnapshot[] states;
        lock (_gate)
        {
            states = _states.Values.Select(state => state with { Stage = UsbStage.Off }).ToArray();
            foreach (var state in states) _states[state.DeviceId] = state;
        }
        foreach (var state in states) Changed?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true; _enabled = false;
            await StopAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class DeviceWorker(UsbDevice device, CancellationTokenSource stop)
    {
        public UsbDevice Device { get; } = device;
        public CancellationTokenSource Stop { get; } = stop;
        public Task Task { get; set; } = Task.CompletedTask;
        public IUsbHostConnection? Connection { get; set; }
        public UsbSnapshot State { get; set; } = new(UsbStage.Negotiating, device.Name, device.InstanceId, device.Driver);
    }
}
