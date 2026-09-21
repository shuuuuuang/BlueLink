using BlueLink.Domain;
using BlueLink.Session;

internal static class TransferAttemptVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
        var stopping = new TransferAttemptRegistry(); var held = stopping.Begin(Guid.NewGuid());
        var drained = stopping.CloseAndDrain();
        Check(!drained.IsCompleted,"exit must wait until snapshot worker actually releases ownership");
        try { stopping.Begin(Guid.NewGuid()); throw new Exception("Accepted send while stopping"); }
        catch (InvalidOperationException) { checks++; }
        held.Dispose(); await drained;
        Check(drained.IsCompletedSuccessfully,"exit completes after the last worker releases ownership");
        var registry = new TransferAttemptRegistry();
        var id = Guid.NewGuid();
        TransferItem Item(TransferStatus status) => new() { Id = id, Name = "qa.bin", TotalBytes = 100, Outgoing = true, Status = status };
        bool Busy() { try { using var unexpected = registry.Begin(id); return false; } catch (InvalidOperationException) { return true; } }
        var ready = new TaskCompletionSource(); var cleanup = new TaskCompletionSource();
        var worker = Task.Run(async () =>
        {
            using var lease = registry.Begin(id);
            lease.Publish(Item(TransferStatus.Canceled), _ => { });
            ready.SetResult(); await cleanup.Task;
        });
        await ready.Task;
        Check(Busy(), "cancel notification must retain ownership until cleanup returns");
        using (registry.Begin(Guid.NewGuid())) Check(true, "other tasks remain independent");
        cleanup.SetResult(); await worker;
        using (registry.Begin(id)) Check(true, "retry becomes possible after actual worker exit");
        var ledger = new SessionTransferLedger(); var outputs = new List<TransferItem>();
        void Report(TransferItem value) { if (ledger.Record(value) is { } result) outputs.Add(result); }
        var old = registry.Begin(id);
        old.Publish(Item(TransferStatus.Queued), Report); var delayed = outputs.Single().Snapshot();
        old.Publish(Item(TransferStatus.Canceled), Report);
        var late = delayed.Snapshot(); late.Status = TransferStatus.Completed;
        Check(ledger.Record(late) is null, "late completion cannot reverse cancellation");
        Check(Busy(), "terminal does not release ownership"); old.Dispose();
        using var current = registry.Begin(id);
        Check(current.Id != old.Id, "retry receives a new attempt ID");
        current.Publish(Item(TransferStatus.Queued), Report);
        Check(ledger.Record(delayed) is null, "retired queue callback cannot replace retry");
        delayed.Status = TransferStatus.Failed;
        Check(ledger.Record(delayed) is null, "retired failure cannot replace retry");
        old.Publish(Item(TransferStatus.Completed), Report);
        current.Publish(Item(TransferStatus.Completed), Report);
        current.Publish(Item(TransferStatus.Failed), Report);
        Check(outputs.Select(value => value.Status).SequenceEqual(new[] { TransferStatus.Queued, TransferStatus.Canceled, TransferStatus.Queued, TransferStatus.Completed }), "exactly one terminal per attempt");
        Check(ledger.Close().Length == 0, "no zombie activity remains");
        old.Dispose(); Check(Busy(), "old repeated disposal cannot release the current owner");
        var unseenLedger = new SessionTransferLedger();
        TransferItem? unseenOld = null;
        var otherId = Guid.NewGuid();
        using (var unseen = registry.Begin(otherId))
            unseen.Publish(new() { Id = otherId, Name = "late.bin", TotalBytes = 1, Outgoing = true, Status = TransferStatus.Queued }, value => unseenOld = value);
        using (var newer = registry.Begin(otherId))
            newer.Publish(new() { Id = otherId, Name = "late.bin", TotalBytes = 1, Outgoing = true, Status = TransferStatus.Queued }, value => unseenLedger.Record(value));
        Check(unseenLedger.Record(unseenOld!) is null, "previously unseen delayed queue cannot replace newer attempt");
        var retryable = Item(TransferStatus.Failed); retryable.PeerId = "peer-a";
        Check(TransferRetryEligibility.Allows(retryable, "PEER-A", true, true), "authenticated same peer can retry");
        Check(!TransferRetryEligibility.Allows(retryable, "peer-b", true, true), "retry cannot be routed to a different peer");
        Check(!TransferRetryEligibility.Allows(retryable, "peer-a", true, false), "revoked trust blocks retry");
        Check(!TransferRetryEligibility.Allows(retryable, "peer-a", false, true), "disconnected peer blocks retry");
        Check(!TransferRetryEligibility.Allows(retryable, null, true, true), "unknown peer blocks retry");
        foreach (var status in Enum.GetValues<TransferStatus>())
        {
            retryable.Status = status;
            Check(TransferRetryEligibility.Allows(retryable, "peer-a", true, true) ==
                (status is TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Canceled), "only retryable terminal statuses are accepted");
        }
        Console.WriteLine($"Attempt ownership verification passed: {checks} checks");
    }
}
