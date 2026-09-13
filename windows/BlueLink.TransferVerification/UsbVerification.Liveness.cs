using System.IO;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;

internal sealed partial class UsbVerification
{
    private async Task VerifyLiveness(string root)
    {
        long time = 0;
        var lease = new UsbSessionLiveness(() => time);
        time = UsbSessionLiveness.TimeoutMs - 1;
        Check(!lease.Expired, "USB lease remains live before deadline");
        lease.Received(); time += UsbSessionLiveness.TimeoutMs - 1;
        Check(!lease.Expired, "authenticated incoming data renews USB lease");
        time++;
        Check(lease.Expired, "USB lease expires exactly at the monotonic deadline");

        await using var a = new SessionSupervisor(new IdentityStore(Path.Combine(root, "live-a")), r => r.Confirm());
        await using var b = new SessionSupervisor(new IdentityStore(Path.Combine(root, "live-b")), r => r.Confirm());
        var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
        await using var leftLifetime = left;
        await using var rightLifetime = right;
        var silent = new SilentUsbStream(left);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.SessionChanged += value => { if (value.Phase == ConnectionPhase.Disconnected) ended.TrySetResult(value.Detail); };
        await a.AddAsync(new StreamConnection(silent, TransportKind.Usb, false), false);
        await b.AddAsync(new StreamConnection(right, TransportKind.Usb, true), true);
        await WaitFor(a, TransportKind.Usb); await WaitFor(b, TransportKind.Usb);
        await Task.Delay(6500);
        Check(a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "idle encrypted USB sessions exchange heartbeats and remain connected");
        try
        {
            await a.SendChatAsync(a.Snapshot().Single().SessionId, "must not route messages through legacy AOA");
            throw new Exception("Expected Bluetooth control route requirement");
        }
        catch (InvalidOperationException)
        {
            Check(true, "legacy AOA-only links cannot become the production message route");
        }
        await a.DisconnectTransportAsync(TransportKind.Usb);
        Check(await ended.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "对端已结束连接。",
            "graceful encrypted GOAWAY disconnects the peer even when native close supplies no EOF");

        var (nextLeft, nextRight) = SecurityHandshakeVerification.MemoryDuplex.Create();
        await using var nextLeftLifetime = nextLeft;
        await using var nextRightLifetime = nextRight;
        var blackhole = new SilentUsbStream(nextLeft);
        ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await a.AddAsync(new StreamConnection(blackhole, TransportKind.Usb, false), false);
        await b.AddAsync(new StreamConnection(nextRight, TransportKind.Usb, true), true);
        await WaitFor(a, TransportKind.Usb); await WaitFor(b, TransportKind.Usb);
        blackhole.DropWrites = true;
        Check((await ended.Task.WaitAsync(TimeSpan.FromSeconds(16))).Contains("无响应"),
            "an abrupt silent USB peer loses readiness within the bounded heartbeat deadline");
    }

    private sealed class SilentUsbStream(Stream inner) : Stream
    {
        public volatile bool DropWrites;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => DropWrites ? ValueTask.CompletedTask : inner.WriteAsync(buffer, token);
        // Emulate accessory mode: host handle release does not electrically unplug the phone.
        protected override void Dispose(bool disposing) { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
