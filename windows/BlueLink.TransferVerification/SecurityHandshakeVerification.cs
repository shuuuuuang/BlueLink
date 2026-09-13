using System.Buffers.Binary;
using System.IO;
using System.Threading.Channels;
using BlueLink.Protocol;
using BlueLink.Security;

internal sealed partial class SecurityHandshakeVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlueLinkSecurityVerification-" + Guid.NewGuid().ToString("N")));
        try
        {
            await Confirmation(root);
            await Rejection(root);
            await Cancellation(root);
            await Revocation(root, false);
            await Revocation(root, true);
            await ChangedIdentity(root);
            await IdentityAssociation(root);
            await ManualPeer(root, "legacy", [1, 0, 0, 0], true);
            await ManualPeer(root, "malformed", [1], false);
            await ManualPeer(root, "incompatible", new ProtocolGreeting(2, 0, BtxCapability.None).Encode(), false);
            await ManualPeer(root, "closed", null, false);
            Console.WriteLine($"BlueLink security handshake verification passed: {_checks} checks");
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("BlueLinkSecurityVerification-", StringComparison.Ordinal))
                throw new IOException("Invalid security verification cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private async Task Confirmation(string root)
    {
        var left = Store(root, "confirm-a"); var right = Store(root, "confirm-b");
        await using (var pair = new Pair(left, right, aName: "蓝联电脑", bName: "验收手机 📱"))
        {
            var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var b = await pair.BRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(a.SafetyCode == b.SafetyCode && a.LocalFingerprint == b.RemoteFingerprint, "matching code and real fingerprints");
            a.Confirm();
            Check(a.Stage == TrustStage.Waiting && left.TrustedIdentities.Count == 0 && right.TrustedIdentities.Count == 0, "local confirmation alone cannot pin either identity");
            b.Confirm();
            var result = await pair.A; var other = await pair.B;
            Check(result.Error is null && other.Error is null && a.Stage == TrustStage.Completed && b.Stage == TrustStage.Completed, "both confirmations and authenticated greetings complete");
            Check(left.MatchesTrustedKey(right.Identity.PeerId, right.Identity.PublicKey) == true && right.MatchesTrustedKey(left.Identity.PeerId, left.Identity.PublicKey) == true, "authenticated identity pins persist");
            Check(result.Value!.Keys.SendKey.SequenceEqual(other.Value!.Keys.ReceiveKey) && result.Value.Negotiation.Minor == 1, "directional keys and BTX/1.1 negotiation");
            Check(result.Value.RemoteDeviceName == "验收手机 📱" && other.Value.RemoteDeviceName == "蓝联电脑", "configured Unicode names arrive only through authenticated greetings");
            a.Cancel(); a.Confirm();
            Check(a.Stage == TrustStage.Completed, "late cancel cannot change a completed handshake");
            await BtxRecordCodec.WriteAsync(pair.Left, new(WireMessageType.GoAway, 0, 0, 1, []), result.Value.Keys.SendKey, result.Value.Keys.SendNoncePrefix, CancellationToken.None);
            var next = await BtxRecordCodec.ReadAsync(pair.Right, other.Value.Keys.ReceiveKey, other.Value.Keys.ReceiveNoncePrefix, other.Value.Replay, CancellationToken.None);
            Check(next.Sequence == 1, "application record continues sequence 1 after greeting");
        }
        await using var cached = new Pair(left, right);
        Check((await cached.A).Error is null && (await cached.B).Error is null && !cached.ARequest.Task.IsCompleted && !cached.BRequest.Task.IsCompleted, "pinned peers reconnect without a prompt");
    }

    private async Task Rejection(string root)
    {
        var left = Store(root, "reject-a"); var right = Store(root, "reject-b");
        await using var pair = new Pair(left, right);
        var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5)); var b = await pair.BRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        b.Cancel();
        Check((await pair.A).Error?.Stage == TrustStage.Rejected && (await pair.B).Error?.Stage == TrustStage.Canceled, "encrypted rejection arrives while local confirmation is open");
        a.Confirm(); b.Confirm();
        Check(a.Stage == TrustStage.Rejected && b.Stage == TrustStage.Canceled && left.TrustedIdentities.Count == 0 && right.TrustedIdentities.Count == 0, "late confirmation cannot revive a rejected request");
    }

    private async Task Cancellation(string root)
    {
        foreach (var cancel in new[] { false, true })
        {
            var left = Store(root, "cancel-a-" + cancel); var right = Store(root, "cancel-b-" + cancel);
            await using var pair = new Pair(left, right, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500));
            var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5)); await pair.BRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            a.Confirm(); if (cancel) pair.Stop.Cancel();
            Check((await pair.A).Error?.Stage == (cancel ? TrustStage.Canceled : TrustStage.TimedOut), "waiting exits with the correct cancellation or timeout");
            a.Confirm();
            Check(left.TrustedIdentities.Count == 0 && right.TrustedIdentities.Count == 0, "canceled and expired confirmation cannot persist trust");
        }
    }

    private async Task Revocation(string root, bool reset)
    {
        var left = Store(root, "revoke-a-" + reset); var right = Store(root, "revoke-b-" + reset);
        await using var pair = new Pair(left, right);
        var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5)); var b = await pair.BRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        a.Confirm();
        if (reset) left.ResetIdentity(); else left.RemoveTrust(Convert.ToHexString(right.Identity.PeerId));
        b.Confirm();
        Check((await pair.A).Error?.Stage == TrustStage.Revoked && left.TrustedIdentities.Count == 0, reset ? "identity reset invalidates in-flight confirmation" : "trust removal invalidates in-flight confirmation");
    }

    private async Task ChangedIdentity(string root)
    {
        var left = Store(root, "changed-a"); var right = Store(root, "changed-b");
        var previous = DeviceIdentity.Generate(); left.Trust(previous.PeerId, previous.PublicKey);
        await using var pair = new Pair(left, right, expected: Convert.ToHexString(previous.PeerId));
        var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await pair.A).Error?.Stage == TrustStage.IdentityChanged && a.TrustedFingerprint == TrustRequest.Fingerprint(previous.PublicKey) && a.RemoteFingerprint == TrustRequest.Fingerprint(right.Identity.PublicKey), "changed key is blocked even when peer ID changes");
        a.Confirm();
        Check(left.TrustedIdentities.Count == 1 && left.MatchesTrustedKey(right.Identity.PeerId, right.Identity.PublicKey) is null, "identity-change prompt cannot replace the original pin");
    }

    private async Task ManualPeer(string root, string label, byte[]? greeting, bool valid)
    {
        var local = Store(root, label); var remote = DeviceIdentity.Generate();
        var (input, output) = MemoryDuplex.Create();
        using (input) using (output) using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var actual = Capture(SecureConnectionHandshake.RunAsync(input, input, false, local, "Remote", request => request.Confirm(), stop.Token));
            var length = new byte[4]; await output.ReadExactlyAsync(length, stop.Token);
            var encoded = new byte[BinaryPrimitives.ReadInt32BigEndian(length)]; await output.ReadExactlyAsync(encoded, stop.Token);
            var hello = HandshakeHello.Create(remote); var keys = hello.Derive(HandshakeHello.Decode(encoded));
            var reply = hello.Encode(); BinaryPrimitives.WriteInt32BigEndian(length, reply.Length);
            await output.WriteAsync(length, stop.Token); await output.WriteAsync(reply, stop.Token);
            if (greeting is null) output.Dispose();
            else await BtxRecordCodec.WriteAsync(output, new(WireMessageType.ProtocolHello, 0, 0, 0, greeting), keys.SendKey, keys.SendNoncePrefix, stop.Token);
            var outcome = await actual;
            Check(valid ? outcome.Value?.Negotiation.Minor == 0 : outcome.Error?.Stage == (greeting is null ? TrustStage.RemoteClosed : TrustStage.Failed), "remote greeting classification: " + label);
            Check(local.TrustedIdentities.Count == (valid ? 1 : 0), "only a valid remote greeting saves trust: " + label);
        }
    }

    private static IdentityStore Store(string root, string name) => new(Path.Combine(root, name));
    private void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException("Security verification failed: " + label); _checks++; }
    private sealed record Outcome(SecureHandshakeResult? Value, TrustHandshakeException? Error);
    private static async Task<Outcome> Capture(Task<SecureHandshakeResult> operation)
    {
        try { return new(await operation, null); }
        catch (TrustHandshakeException error) { return new(null, error); }
    }
    private sealed class Pair : IAsyncDisposable
    {
        public readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(8));
        public readonly TaskCompletionSource<TrustRequest> ARequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<TrustRequest> BRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryDuplex Left { get; }
        public MemoryDuplex Right { get; }
        public Task<Outcome> A { get; }
        public Task<Outcome> B { get; }
        public Pair(IdentityStore a, IdentityStore b, TimeSpan? timeout = null, string? expected = null, IdentityAssociationHandler? association = null, string? aName = null, string? bName = null)
        {
            (Left, Right) = MemoryDuplex.Create();
            A = Capture(SecureConnectionHandshake.RunAsync(Left, Left, false, a, "Phone", r => ARequest.TrySetResult(r), Stop.Token, expected, timeout, association, localDeviceName: aName));
            B = Capture(SecureConnectionHandshake.RunAsync(Right, Right, true, b, "PC", r => BRequest.TrySetResult(r), Stop.Token, timeout: timeout, localDeviceName: bName));
        }
        public async ValueTask DisposeAsync() { Stop.Cancel(); Left.Dispose(); Right.Dispose(); await Task.WhenAll(A, B); Stop.Dispose(); }
    }
    internal sealed class MemoryDuplex(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : Stream
    {
        private ReadOnlyMemory<byte> _pending;
        public static (MemoryDuplex, MemoryDuplex) Create()
        {
            var left = Channel.CreateUnbounded<byte[]>(); var right = Channel.CreateUnbounded<byte[]>();
            return (new(left.Reader, right.Writer), new(right.Reader, left.Writer));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            while (_pending.IsEmpty) { if (!await reader.WaitToReadAsync(token)) return 0; if (reader.TryRead(out var bytes)) _pending = bytes; }
            var size = Math.Min(buffer.Length, _pending.Length); _pending[..size].CopyTo(buffer); _pending = _pending[size..]; return size;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken token = default) => writer.WriteAsync(value.ToArray(), token);
        protected override void Dispose(bool disposing) { if (disposing) writer.TryComplete(); base.Dispose(disposing); }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
