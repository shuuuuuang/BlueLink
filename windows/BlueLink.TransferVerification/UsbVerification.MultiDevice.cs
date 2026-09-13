using System.Collections.Concurrent;
using System.IO;
using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Transport;
using BlueLink.Usb;

internal sealed partial class UsbVerification
{
    private void VerifyReadiness()
    {
        var ready = new SessionSnapshot(Guid.NewGuid(), "peer-a", "A", "bt:a", ConnectionPhase.Connected, "", DateTimeOffset.Now, TransportKind.Bluetooth) { UsbFileReady = true };
        Check(UsbSessionPolicy.IsReady(true, "PEER-A", [ready]), "authenticated peer matching is case insensitive");
        Check(!UsbSessionPolicy.IsReady(false, "peer-a", [ready]), "switch off immediately suppresses readiness");
        Check(!UsbSessionPolicy.IsReady(true, "peer-b", [ready]), "one peer cannot borrow another peer USB capability");
        Check(!UsbSessionPolicy.IsReady(true, null, [ready]), "unidentified peers cannot show lightning");
        foreach (var phase in Enum.GetValues<ConnectionPhase>().Where(value => value != ConnectionPhase.Connected))
            Check(!UsbSessionPolicy.IsReady(true, "peer-a", [ready with { Phase = phase }]), "no lightning before authentication or after disconnect: " + phase);
        Check(!UsbSessionPolicy.IsReady(true, "peer-a", [ready with { UsbFileReady = false }]), "Bluetooth alone cannot show lightning");
        var lost = new UsbSnapshot(UsbStage.Fallback) { PeerId = "peer-a" };
        Check(UsbSessionPolicy.Notice(true, "peer-a", [ready with { UsbFileReady = false }], [lost]) == "USB 连接已断开，新传输将使用蓝牙。", "fallback notice requires live same-peer Bluetooth");
        Check(UsbSessionPolicy.Notice(true, "peer-a", [], [lost]) == "USB 连接已断开，设备当前离线。", "stale fallback state cannot advertise missing Bluetooth");
        Check(UsbSessionPolicy.Notice(true, "peer-b", [], [lost]) is null && UsbSessionPolicy.Notice(false, "peer-a", [], [lost]) is null, "notices are scoped to an enabled matching peer");
        Check(UsbSessionPolicy.Notice(true, "peer-a", [ready], [lost]) is null, "reconnected USB clears an old disconnect notice");
    }

    private async Task VerifyConcurrentHosts()
    {
        var backend = new MultiBackend();
        UsbHost? host = null;
        var accepted = new ConcurrentDictionary<string, MultiConnection>();
        host = new(backend, () => "qa-host", connection =>
        {
            var peer = connection.PeerName;
            accepted[peer] = (MultiConnection)connection;
            host!.ObserveSession(new(Guid.NewGuid(), peer, peer, connection.TransportAddress,
                ConnectionPhase.Connected, "", DateTimeOffset.Now, TransportKind.Usb));
            return Task.CompletedTask;
        }, peer => peer == "a", TimeSpan.FromMilliseconds(20));
        await using (host)
        {
            await host.SetEnabledAsync(true);
            await Until(() => host.Snapshots.Count(value => value.PeerId is "a" or "b" && value.IsReady) == 2 &&
                host.Snapshots.Any(value => value.Stage == UsbStage.PermissionDenied));
            Check(host.Snapshots.Count(value => value.PeerId is "a" or "b" && value.IsReady) == 2,
                "two live USB devices negotiate concurrently despite a third permission failure");
            Check(host.Snapshots.Single(value => value.DeviceId == "denied").PeerId is null,
                "a failed unidentified device is never assigned a nearby conversation");
            var oldA = accepted["a"];
            backend.Present = ["b", "denied"];
            await Until(() => oldA.Disposed);
            Check(!accepted["b"].Disposed && host.Snapshots.Single(value => value.PeerId == "b").IsReady,
                "unplugging A closes only its own worker and leaves B ready");
            Check(host.Snapshots.Single(value => value.PeerId == "a").Stage == UsbStage.Fallback,
                "only the disconnected peer's real Bluetooth connection permits fallback");
            backend.Present = ["a", "b", "denied"];
            await Until(() => accepted["a"] != oldA);
            Check(backend.Opens["denied"] == 1, "failed access is not retried on every inventory poll");
            await host.SetEnabledAsync(false);
            Check(accepted.Values.All(value => value.Disposed) && host.Snapshots.All(value => value.Stage == UsbStage.Off),
                "disabling USB closes all workers without stale ready states");
            var enumerations = backend.Enumerations;
            await Task.Delay(80);
            Check(backend.Enumerations == enumerations, "disabled host stops all native inventory polling");
            var oldB = accepted["b"];
            await host.SetEnabledAsync(true);
            await Until(() => accepted["b"] != oldB && backend.Opens["denied"] == 2);
            Check(!accepted["b"].Disposed, "off/on starts fresh per-device lifetimes");
        }
        Check(accepted.Values.All(value => value.Disposed), "disposing host releases every open USB connection");
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed class MultiBackend : IUsbHostBackend
    {
        public volatile string[] Present = ["a", "b", "denied"];
        public ConcurrentDictionary<string, int> Opens { get; } = new();
        public int Enumerations;
        public Task<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Enumerations);
            return Task.FromResult<IReadOnlyList<UsbDevice>>(Present.Select(id => new UsbDevice(id, id, "usb:" + id, "WinUSB", true, true)).ToArray());
        }
        public Task<IUsbHostConnection> OpenAsync(UsbDevice device, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Opens.AddOrUpdate(device.InstanceId, 1, (_, n) => n + 1);
            if (device.InstanceId == "denied") throw new UsbFailure(UsbStage.PermissionDenied, "QA denied");
            return Task.FromResult<IUsbHostConnection>(new MultiConnection(device.InstanceId));
        }
    }
    private sealed class MultiConnection(string peer) : IUsbHostConnection
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => _closed.Task.IsCompleted;
        public Task Closed => _closed.Task;
        public UsbLinkSpeed Speed => UsbLinkSpeed.Unknown;
        public Stream Input => Stream.Null;
        public Stream Output => Stream.Null;
        public string PeerName => peer;
        public string TransportAddress => "usb:" + peer;
        public bool ListenerRole => true;
        public TransportKind Transport => TransportKind.Usb;
        public PeerPlatform Platform => PeerPlatform.Android;
        public Task<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, byte[] bytes, CancellationToken token) => throw new InvalidOperationException("Accessory interface needs no mode switch");
        public ValueTask DisposeAsync() { _closed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
