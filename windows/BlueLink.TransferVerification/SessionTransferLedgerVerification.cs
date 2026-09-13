using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Usb;

internal static class SessionTransferLedgerVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string detail) { if (!value) throw new Exception(detail); checks++; }
        foreach (var outgoing in new[] { false, true })
        foreach (var status in Enum.GetValues<TransferStatus>())
        {
            var value = new TransferItem { Id = Guid.NewGuid(), Name = "qa.bin", TotalBytes = 1024, CompletedBytes = 512,
                Status = status, Outgoing = outgoing, PeerId = "peer-a", LocalPath = "qa-source", MessageId = Guid.NewGuid() };
            var ledger = new SessionTransferLedger();
            var published = ledger.Record(value)!;
            var active = value.IsActive;
            var interrupted = ledger.Close();
            Check(interrupted.Length == (active ? 1 : 0), "disconnect accounts for every active phase");
            if (active)
            {
                var failed = interrupted.Single();
                Check(failed.Status == TransferStatus.Failed && !failed.CanResume && !failed.IsActive, "stale pause must end");
                Check(failed.Id == value.Id && failed.CompletedBytes == 512 && failed.MessageId == value.MessageId &&
                    failed.LocalPath == value.LocalPath, "identity/source/checkpoint retained for retry");
                value.Status = TransferStatus.Paused;
                Check(failed.Status == TransferStatus.Failed && published.Status == TransferStatus.Failed, "old mutable source is detached");
                var replacement = new SessionTransferLedger();
                var retry = SessionTransferLedger.Copy(failed); retry.Status = TransferStatus.Queued;
                Check(replacement.Record(retry)!.IsActive, "new session accepts same transfer ID");
                Check(ledger.Record(value) is null, "closed owner cannot affect retry");
                retry.Status = TransferStatus.Canceled;
                Check(replacement.Record(retry)!.Status == TransferStatus.Canceled && replacement.Close().Length == 0,
                    "cancel works in the new session");
            }
            Check(ledger.Record(value) is null && ledger.Close().Length == 0, "late reports and duplicate closure ignored");
        }
        var queue = new DeviceFileQueue();
        var paused = new TransferItem { Id = Guid.NewGuid(), Name = "paused.bin", TotalBytes = 1024,
            Outgoing = true, Status = TransferStatus.Transferring };
        var progress = new TransferPauseController(paused, _ => { });
        progress.SetPaused(true, true);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var first = queue.RunAsync(async token => { entered.SetResult(); await progress.WaitAsync(token); }, stop.Token);
        await entered.Task;
        var second = queue.RunAsync(_ => Task.CompletedTask, default);
        Check(!second.IsCompleted, "next file waits behind paused head");
        stop.Cancel();
        try { await first; throw new Exception("paused worker ignored session closure"); } catch (OperationCanceledException) { }
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Check(queue.Count == 0, "disconnect releases queue");
        progress.Report(TransferStatus.Failed);
        progress.Report(TransferStatus.Transferring, 800);
        progress.Report(TransferStatus.Canceled);
        Check(paused.Status == TransferStatus.Failed && paused.CompletedBytes == 0, "late progress cannot replace final result");
        await TransferPauseVerification.RunAsync();
        Console.WriteLine($"Reconnect ownership, retry, cancellation and queue regression passed: {checks} checks");
    }
}
