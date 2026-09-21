using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;

internal sealed partial class UsbVerification
{
    public async Task VerifyTransferReconnectAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        foreach (var cancelRetry in new[] { false, true })
        {
            var run = Path.Combine(root, cancelRetry ? "cancel" : "resume");
            Directory.CreateDirectory(run);
            await using var sender = new SessionSupervisor(new IdentityStore(Path.Combine(run, "sender")), request => request.Confirm());
            await using var receiver = new SessionSupervisor(new IdentityStore(Path.Combine(run, "receiver")), request => request.Confirm());
            sender.OutgoingDirectory = Path.Combine(run, "outgoing"); receiver.ReceiveDirectory = Path.Combine(run, "received");
            var sent = Channel.CreateUnbounded<TransferItem>(); var received = Channel.CreateUnbounded<TransferItem>();
            sender.TransferChanged += (_, item) => sent.Writer.TryWrite(item);
            receiver.TransferChanged += (_, item) => received.Writer.TryWrite(item);
            var gate = await Link(holdAfter: 1);
            var source = Path.Combine(run, "reconnect.bin"); var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
            await File.WriteAllBytesAsync(source, bytes);
            var oldRoute = await WaitFor(sender, TransportKind.Bluetooth);
            var sending = sender.SendFileAsync(oldRoute.SessionId, source);
            await gate!.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var offered = await Read(sent.Reader, item => item.IsActive);
            var pause = sender.PauseTransferAsync(oldRoute.SessionId, offered.Id);
            await Read(sent.Reader, item => item.Status == TransferStatus.Paused);
            gate.Release.TrySetResult(); await pause.WaitAsync(TimeSpan.FromSeconds(5));
            await Read(received.Reader, item => item.Status == TransferStatus.RemotePaused);
            await sender.DisconnectAsync(oldRoute.SessionId).WaitAsync(TimeSpan.FromSeconds(5));
            var failed = await Read(sent.Reader, item => item.Status == TransferStatus.Failed);
            await Read(received.Reader, item => item.Status == TransferStatus.Failed);
            try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("interrupted send completed"); }
            catch (OperationCanceledException) { } catch (IOException) { }
            Check(failed.Id == offered.Id && !failed.IsActive && !failed.CanResume, "disconnect replaces paused controls with failure/retry");
            await Until(() => sender.ActiveCount == 0 && receiver.ActiveCount == 0);
            var retryGate = await Link(cancelRetry ? 0 : null);
            var newRoute = await WaitFor(sender, TransportKind.Bluetooth);
            Check(newRoute.SessionId != oldRoute.SessionId && newRoute.PeerId == oldRoute.PeerId, "reconnect creates a new live owner for same authenticated peer");
            var retriedEvents = 0;
            void CountRetry(SessionSnapshot _, TransferItem item) { if (item.Id == failed.Id) Interlocked.Increment(ref retriedEvents); }
            sender.TransferChanged += CountRetry;
            try
            {
                var changed = bytes.ToArray(); changed[0] ^= 1;
                await File.WriteAllBytesAsync(source, changed);
                try { await sender.RetryFileAsync(newRoute.SessionId, source, failed); throw new Exception("changed source was submitted"); }
                catch (InvalidOperationException) { }
                Check(retriedEvents == 0, "same-size replacement is rejected before any queued event or offer");
                await File.WriteAllBytesAsync(source, bytes);
            }
            finally { sender.TransferChanged -= CountRetry; }
            var retry = sender.RetryFileAsync(newRoute.SessionId, source, failed);
            var resumed = await Read(sent.Reader, item => item.Status == TransferStatus.Resuming);
            Check(resumed.AttemptId is not null && resumed.AttemptId != offered.AttemptId, "actual retry has a distinct attempt identity");
            Check(resumed.Id == offered.Id && resumed.CompletedBytes >= 65536, "same file ID resumes from receiver's saved extent");
            if (cancelRetry)
            {
                await retryGate!.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var cancel = sender.CancelTransferAsync(newRoute.SessionId, offered.Id);
                retryGate.Release.TrySetResult(); await cancel.WaitAsync(TimeSpan.FromSeconds(5));
                await Read(sent.Reader, item => item.Status == TransferStatus.Canceled);
                await Read(received.Reader, item => item.Status == TransferStatus.Canceled);
                try { await retry.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("canceled retry completed"); } catch (OperationCanceledException) { }
                Check(true, "cancel after reconnect reaches both actual peer sessions");
            }
            else
            {
                await retry.WaitAsync(TimeSpan.FromSeconds(10));
                var complete = await Read(received.Reader, item => item.Status == TransferStatus.Completed);
                Check((await File.ReadAllBytesAsync(complete.LocalPath!)).SequenceEqual(bytes), "resumed file is byte-identical");
            }
            async Task<ReconnectGate?> Link(int? holdAfter)
            {
                var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
                var held = holdAfter is { } count ? new ReconnectGate(left, count) : null;
                await sender.AddAsync(new StreamConnection((Stream?)held ?? left, TransportKind.Bluetooth, false), false);
                await receiver.AddAsync(new StreamConnection(right, TransportKind.Bluetooth, true), true);
                await WaitFor(receiver, TransportKind.Bluetooth);
                return held;
            }
        }
        Console.WriteLine($"Encrypted peer pause/disconnect/reconnect/resume/cancel passed: {_checks} checks");
    }
    private static async Task<TransferItem> Read(ChannelReader<TransferItem> reader, Func<TransferItem, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true) { var value = await reader.ReadAsync(timeout.Token); if (predicate(value)) return value; }
    }
    private sealed class ReconnectGate(Stream inner, int pass) : Stream
    {
        private int _remaining = pass;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length > 8192 && Interlocked.Decrement(ref _remaining) == -1)
            { Blocked.TrySetResult(); await Release.Task.WaitAsync(token); }
            await inner.WriteAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) { Release.TrySetResult(); inner.Dispose(); } base.Dispose(disposing); }
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b,int o,int c) => throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
