using System.IO;
using System.Threading.Channels;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;

internal sealed partial class UsbVerification
{
    public async Task VerifyComposerOrderAsync(string directory)
    {
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        await using var sender = new SessionSupervisor(new IdentityStore(Path.Combine(root, "sender")), r => r.Confirm());
        await using var receiver = new SessionSupervisor(new IdentityStore(Path.Combine(root, "receiver")), r => r.Confirm());
        sender.OutgoingDirectory = Path.Combine(root, "outgoing"); receiver.ReceiveDirectory = Path.Combine(root, "received");
        var senderRecoveryRoot = Path.Combine(root, "sender-recovery");
        var receiverRecoveryRoot = Path.Combine(root, "receiver-recovery");
        var senderRecovery = new BlueLink.Storage.TransferRecoveryStore(senderRecoveryRoot);
        var receiverRecovery = new BlueLink.Storage.TransferRecoveryStore(receiverRecoveryRoot);
        sender.PersistRecovery = (session,item) => senderRecovery.RecordTransfer(session.SessionId,session.StartedAt,session.Transport.ToString(),item);
        receiver.PersistRecovery = (session,item) => receiverRecovery.RecordTransfer(session.SessionId,session.StartedAt,session.Transport.ToString(),item);
        var messages = Channel.CreateUnbounded<ChatEnvelope>();
        receiver.EnvelopeReceived += (_, envelope, outgoing) => { if (!outgoing) messages.Writer.TryWrite(envelope); };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accept = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<BlueLink.Transfer.IncomingFileDecision, System.Threading.CancellationToken, Task<string?>> receiveDecision = (_, token) => { waiting.TrySetResult(); return accept.Task.WaitAsync(token); };
        receiver.ReceiveDecision = (offer,token) => receiveDecision(offer,token);
        receiver.AutoAcceptFiles = false;
        var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
        await sender.AddAsync(new StreamConnection(left, TransportKind.Bluetooth, false), false);
        await receiver.AddAsync(new StreamConnection(right, TransportKind.Bluetooth, true), true);
        var route = await WaitFor(sender, TransportKind.Bluetooth); await WaitFor(receiver, TransportKind.Bluetooth);
        var source = Path.Combine(root, "source.bin"); await File.WriteAllBytesAsync(source, new byte[1024]);
        await sender.SendChatAsync(route.SessionId, "text1");
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = sender.SendFileAsync(route.SessionId, source, "report.pdf", () => announced.TrySetResult());
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10)); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(new BlueLink.Storage.TransferRecoveryStore(senderRecoveryRoot).MergeHistory([]).Single().RecoveryPending,
            "sender recovery is durable before receiver accepts the file");
        Check(!transfer.IsCompleted, "file announcement does not wait for receiver confirmation or payload transfer");
        await sender.SendChatAsync(route.SessionId, "text2").WaitAsync(TimeSpan.FromSeconds(5));
        var destination = Path.Combine(receiver.ReceiveDirectory, "report.pdf"); Directory.CreateDirectory(receiver.ReceiveDirectory);
        accept.TrySetResult(destination);
        var actual = new List<ChatEnvelope>();
        for (var i = 0; i < 3; i++) actual.Add(await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Check(actual[0].Body == "text1" && actual[1].Attachments.Count > 0 && actual[2].Body == "text2", "encrypted receiver sees text / file / text in composer order");
        await transfer.WaitAsync(TimeSpan.FromSeconds(10));
        Check(File.Exists(destination) && (await File.ReadAllBytesAsync(destination)).Length == 1024, "staged display name and file payload reach receiver");
        Check(new BlueLink.Storage.TransferRecoveryStore(senderRecoveryRoot).MergeHistory([]).Count == 0 &&
            new BlueLink.Storage.TransferRecoveryStore(receiverRecoveryRoot).MergeHistory([]).Count == 0,
            "encrypted completion clears pending recovery on both ends");
        var rejectedEnvelope = false;
        receiver.EnvelopeReceived += (_, envelope, _) => { if (envelope.Attachments.Any(a => a.FileName == "QA-disk-full.bin")) rejectedEnvelope = true; };
        TransferItem? lastFailed = null;
        sender.TransferChanged += (_,item) => { if (item.Name == "QA-disk-full.bin") lastFailed = item; };
        sender.PersistRecovery = (_,_) => throw new IOException("Injected full disk");
        try { await sender.SendFileAsync(route.SessionId, source, "QA-disk-full.bin"); throw new Exception("Expected persistence failure"); }
        catch (IOException) { }
        Check(!rejectedEnvelope && lastFailed?.Status == TransferStatus.Failed,
            "initial persistence failure stops before announcement and publishes a non-active failure");
        sender.PersistRecovery = (session,item) => senderRecovery.RecordTransfer(session.SessionId,session.StartedAt,session.Transport.ToString(),item);
        var decisions = 0;
        receiveDecision = (_,_) => Task.FromResult<string?>(++decisions == 1 ? null : Path.Combine(receiver.ReceiveDirectory,"QA-retry.bin"));
        TransferItem? retryTemplate = null;
        sender.TransferChanged += (_,item) => { if (item.Name == "QA-retry.bin") retryTemplate = item; };
        try { await sender.SendFileAsync(route.SessionId,source,"QA-retry.bin").WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { }
        Check(retryTemplate is { IsRetryableTerminal: true },"receiver refusal leaves an explicitly retryable task");
        await sender.RetryFileAsync(route.SessionId,source,retryTemplate!).WaitAsync(TimeSpan.FromSeconds(10));
        Check(decisions == 2 && retryTemplate?.Status == TransferStatus.Completed,
            "same-session retry completes with a new local attempt");
        Check((await File.ReadAllBytesAsync(Path.Combine(receiver.ReceiveDirectory,"QA-retry.bin"))).SequenceEqual(await File.ReadAllBytesAsync(source)),
            "same-session recovered payload matches original bytes");
        Check(new BlueLink.Storage.TransferRecoveryStore(receiverRecoveryRoot).MergeHistory([]).Count == 0,
            "receiver recovery journal does not retain the rejected previous attempt");
        Console.WriteLine($"Composer encrypted transport passed: {_checks} checks; no UI or physical connection");
    }
}
