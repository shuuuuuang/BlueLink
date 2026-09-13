using System.IO;
using System.Threading.Channels;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transfer;
using BlueLink.Transport;

internal static class ReceiveSettingsVerification
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkReceiveSettings-" + Guid.NewGuid().ToString("N"));
        var logging = SessionLog.Enabled; SessionLog.Enabled = false;
        Directory.CreateDirectory(root);
        try { await Verify(root); }
        finally
        {
            SessionLog.Enabled = logging;
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("BlueLinkReceiveSettings-")) throw new IOException("Invalid test root");
            Directory.Delete(root, true);
        }
    }
    private static async Task Verify(string root)
    {
        await using var sender = new SessionSupervisor(new IdentityStore(Path.Combine(root, "sender")), r => r.Confirm());
        await using var receiver = new SessionSupervisor(new IdentityStore(Path.Combine(root, "receiver")), r => r.Confirm());
        sender.OutgoingDirectory = Path.Combine(root, "outgoing");
        receiver.ReceiveDirectory = Path.Combine(root, "received");
        Directory.CreateDirectory(receiver.ReceiveDirectory);
        var requests = Channel.CreateUnbounded<IncomingFileDecision>();
        var canceled = Channel.CreateUnbounded<Guid>();
        var chats = Channel.CreateUnbounded<string>();
        var sentStates = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferStatus>();
        var receivedStates = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferStatus>();
        sender.TransferChanged += (_, item) => sentStates[item.Id] = item.Status;
        receiver.TransferChanged += (_, item) => receivedStates[item.Id] = item.Status;
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ReceiveDecision = async (request, token) => {
            requests.Writer.TryWrite(request);
            try { return await answer.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.Writer.TryWrite(request.Offer.Id); throw; }
        };
        receiver.MessageReceived += (_, item) => chats.Writer.TryWrite(item.Text);
        var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
        await sender.AddAsync(new Connection(left), false);
        await receiver.AddAsync(new Connection(right), true);
        await Until(() => sender.Snapshot().Any(s => s.Phase == ConnectionPhase.Connected) && receiver.Snapshot().Any(s => s.Phase == ConnectionPhase.Connected));
        var id = sender.Snapshot().Single().SessionId;
        var checks = 0;
        void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); checks++; }
        async Task<string> Source(string name) { var file = Path.Combine(root, name); await File.WriteAllTextAsync(file, "approved bytes"); return file; }
        async Task<IncomingFileDecision> Request() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        async Task Rejected(Task sending, bool mayTimeout = false) { try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Rejected file unexpectedly sent"); } catch (Exception ex) when ((ex is IOException or OperationCanceledException) || (mayTimeout && ex is TimeoutException)) { } }

        await sender.SendFileAsync(id, await Source("automatic.txt")).WaitAsync(TimeSpan.FromSeconds(5));
        Check(File.Exists(Path.Combine(receiver.ReceiveDirectory, "automatic.txt")) && !requests.Reader.TryRead(out _), "automatic receiving must not prompt");

        receiver.AutoAcceptFiles = false;
        var sending = sender.SendFileAsync(id, await Source("confirmed.txt"));
        var request = await Request();
        Check(request.RequiresConfirmation && !File.Exists(Path.Combine(receiver.ReceiveDirectory, "confirmed.txt")), "manual receive waits before creating the received file");
        await sender.SendChatAsync(id, "still responsive");
        Check(await chats.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == "still responsive", "confirmation must not block encrypted chat");
        answer.TrySetResult("rename"); await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.ReceiveDirectory, "confirmed.txt")) == "approved bytes", "accepted file completes intact");

        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sending = sender.SendFileAsync(id, await Source("declined.txt")); await Request(); answer.TrySetResult(null); await Rejected(sending);
        Check(!File.Exists(Path.Combine(receiver.ReceiveDirectory, "declined.txt")), "declined file never lands");

        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sending = sender.SendFileAsync(id, await Source("canceled.txt")); request = await Request();
        await sender.CancelTransferAsync(id, request.Offer.Id); await Rejected(sending);
        Check(await canceled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == request.Offer.Id, "remote cancellation dismisses pending confirmation");
        Check(!File.Exists(Path.Combine(receiver.ReceiveDirectory, "canceled.txt")), "remote canceled file never lands");
        await Until(() => receivedStates.TryGetValue(request.Offer.Id, out var status) && status == TransferStatus.Canceled);
        Check(sentStates[request.Offer.Id] == TransferStatus.Canceled, "canceling an outgoing offer stays canceled instead of becoming failed");
        Check(receivedStates[request.Offer.Id] == TransferStatus.Canceled, "remote cancellation is not overwritten by a second failure frame");

        receiver.MaxReceiveBytes = 1;
        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oversized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TransferChanged += (_, item) => { if (item.Status == TransferStatus.Rejected) oversized.TrySetResult(); };
        sending = sender.SendFileAsync(id, await Source("limit.txt")); await oversized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!requests.Reader.TryRead(out _), "size limit is checked before confirmation");
        receiver.MaxReceiveBytes = 1000; request = await Request(); answer.TrySetResult("rename"); await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(request.RequiresConfirmation && File.Exists(Path.Combine(receiver.ReceiveDirectory, "limit.txt")), "raising the limit still requires manual acceptance");

        receiver.AutoAcceptFiles = true; receiver.DuplicateFilePolicy = "ask";
        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await File.WriteAllTextAsync(Path.Combine(receiver.ReceiveDirectory, "duplicate.txt"), "original");
        sending = sender.SendFileAsync(id, await Source("duplicate.txt")); request = await Request();
        Check(request.NameConflict && !request.RequiresConfirmation, "duplicate policy asks even with automatic receiving");
        answer.TrySetResult("rename"); await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.ReceiveDirectory, "duplicate.txt")) == "original" && File.Exists(Path.Combine(receiver.ReceiveDirectory, "duplicate (1).txt")), "duplicate choice preserves the original");
        receiver.AutoAcceptFiles = false;
        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sending = sender.SendFileAsync(id, await Source("timeout.txt")); request = await Request();
        Check(await canceled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(35)) == request.Offer.Id,
            "unanswered request expires at the real receive deadline");
        await Rejected(sending, mayTimeout: true);
        Check(!File.Exists(Path.Combine(receiver.ReceiveDirectory, "timeout.txt")), "expired confirmation creates no file");

        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sending = sender.SendFileAsync(id, await Source("disconnected.txt")); request = await Request();
        await receiver.DisconnectAsync(receiver.Snapshot().Single().SessionId);
        Check(await canceled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == request.Offer.Id,
            "disconnect cancels pending confirmation");
        await Rejected(sending);
        Check(!File.Exists(Path.Combine(receiver.ReceiveDirectory, "disconnected.txt")), "disconnect creates no received file");
        Console.WriteLine($"Settings receive verification passed: {checks} checks over real encrypted in-memory sessions.");
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Connection(Stream stream) : IPeerConnection
    {
        public Stream Input => stream;
        public Stream Output => stream;
        public string PeerName => "Settings test peer";
        public string TransportAddress => "settings:memory";
        public bool ListenerRole => false;
        public TransportKind Transport => TransportKind.Bluetooth;
        public PeerPlatform Platform => PeerPlatform.Windows;
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
