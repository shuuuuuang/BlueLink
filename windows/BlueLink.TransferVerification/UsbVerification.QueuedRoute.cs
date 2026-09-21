using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;
using BlueLink.Usb;

internal sealed partial class UsbVerification
{
    public async Task VerifyQueuedRouteAsync(string directory)
    {
        var gate = new QueuedRouteGate();
        var decision = gate.Request()!;
        var start = gate.StartAsync(CancellationToken.None);
        Check(!start.IsCompleted && gate.Request() is null, "switch freezes stream opening and rejects duplicate requests");
        gate.Resolve(true);
        Check(await decision, "receiver confirmation accepts switch");
        try { await start; throw new Exception("Old stream opened"); }
        catch (OperationCanceledException) { Check(true, "accepted switch prevents original stream opening"); }
        gate = new QueuedRouteGate(); decision = gate.Request()!; start = gate.StartAsync(CancellationToken.None);
        gate.Resolve(false); await start;
        Check(!await decision && gate.Request() is null, "denial preserves original queue and started stream cannot switch");
        var registry = new TransferAttemptRegistry(); var taskId = Guid.NewGuid();
        using (var lease = registry.Begin(taskId))
        {
            var released = registry.WhenReleased(taskId);
            lease.Publish(new TransferItem { Id = taskId, Name = "test", TotalBytes = 1, Outgoing = true, Status = TransferStatus.Canceled }, _ => { });
            Check(!released.IsCompleted, "terminal UI status does not release physical worker ownership");
        }
        Check(registry.WhenReleased(taskId).IsCompleted, "worker disposal releases ownership");

        var fence = new TransferStreamFence();
        for (var i = 0; i < 16384; i++) fence.StartOutgoing(taskId, true, true).Dispose();
        try { fence.StartOutgoing(taskId, true, true); throw new Exception("Unbounded attempts"); }
        catch (IOException) { Check(true, "repeated retries have bounded session memory"); }
        Check(fence.Receive(Guid.NewGuid(), 99999, true, false, true, true) is null, "peer cannot extend exhausted attempt capacity");

        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        // USB-labelled memory streams suppress physical MTP discovery. Queues are held so
        // the test exercises authenticated control messages without ever opening WPD.
        await using var a = new PeerSession(new StreamConnection(left, TransportKind.Usb, false), false,
            new IdentityStore(Path.Combine(root, "a")), t => t.Confirm(), _ => { },
            t => { if (t.QueuedForUsb) queued.TrySetResult(t.Snapshot()); if (t.Status == TransferStatus.Completed) completed.TrySetResult(t.Snapshot()); },
            _ => aReady.TrySetResult(), _ => { }) { OutgoingDirectory = Path.Combine(root, "snapshots") };
        await using var b = new PeerSession(new StreamConnection(right, TransportKind.Usb, true), true,
            new IdentityStore(Path.Combine(root, "b")), t => t.Confirm(), _ => { },
            t => { if (t.Status == TransferStatus.Queued) receiverQueued.TrySetResult(); if (t.Status == TransferStatus.Completed) received.TrySetResult(t.Snapshot()); },
            _ => bReady.TrySetResult(), _ => { }, receiveDirectory: Path.Combine(root, "received"));
        var runA = a.RunAsync(); var runB = b.RunAsync();
        await Task.WhenAll(aReady.Task, bReady.Task).WaitAsync(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueA = new DeviceFileQueue(); var queueB = new DeviceFileQueue();
        var holdA = queueA.RunAsync(_ => release.Task, CancellationToken.None);
        var holdB = queueB.RunAsync(_ => release.Task, CancellationToken.None);
        var epoch = Guid.NewGuid().ToString("N");
        Configure(a, queueA); Configure(b, queueB);
        try
        {
            var payload = RandomNumberGenerator.GetBytes(192 * 1024);
            var file = Path.Combine(root, "route.bin"); await File.WriteAllBytesAsync(file, payload);
            var original = a.SendFileAsync(file);
            var before = await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await receiverQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var jobs = typeof(PeerSession).GetField("_mtpJobs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b)!;
            var incomingJob = jobs.GetType().GetProperty("Item")!.GetValue(jobs, new object[] { before.Id })!;
            var importing = incomingJob.GetType().GetField("ImportStarted")!;
            importing.SetValue(incomingJob, true); // receiver has crossed its stream-opening boundary
            try { await a.SwitchQueuedToBluetoothAsync(before).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected switch denial"); }
            catch (InvalidOperationException) { Check(!original.IsCompleted, "receiver denial preserves the original outgoing queue"); }
            importing.SetValue(incomingJob, false);
            await a.SwitchQueuedToBluetoothAsync(before).WaitAsync(TimeSpan.FromSeconds(20));
            try { await original; } catch (OperationCanceledException) { }
            var after = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var saved = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(after.Id == before.Id && after.AttemptId != before.AttemptId, "switch retains task identity and starts a fresh attempt");
            Check(saved.Id == before.Id && (await File.ReadAllBytesAsync(saved.LocalPath!)).SequenceEqual(payload), "authenticated switched transfer publishes original bytes exactly once");
            Check(!holdA.IsCompleted && !holdB.IsCompleted && queueA.Count == 1 && queueB.Count == 1, "switch releases both queued jobs without opening a physical file stream");
        }
        finally
        {
            release.TrySetResult(); await Task.WhenAll(holdA, holdB);
            await a.DisposeAsync(); await b.DisposeAsync();
            await Task.WhenAll(runA, runB).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine($"Queued route verification passed: {_checks} checks");

        void Configure(PeerSession session, DeviceFileQueue queue)
        {
            var type = typeof(PeerSession);
            type.GetField("_mtpEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, true);
            type.GetField("_wpd", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, new WpdBinding("qa-unused", "qa-unused", queue));
            var packetType = type.GetNestedType("MtpPacket", BindingFlags.NonPublic)!;
            var packet = Activator.CreateInstance(packetType, "announce")!;
            packetType.GetProperty("Epoch")!.SetValue(packet, epoch);
            type.GetField("_mtpAnnouncement", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, packet);
        }
    }
}
