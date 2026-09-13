using BlueLink.Domain;
using BlueLink.Session;

internal static class TransferPauseVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Pause regression failed at " + checks); checks++; }
        foreach (var outgoing in new[] { false, true })
        {
            var item = new TransferItem { Id = Guid.NewGuid(), Name = "pause.bin", TotalBytes = 1000, Outgoing = outgoing,
                Status = TransferStatus.Transferring, CompletedBytes = 300 };
            var progress = new TransferPauseController(item, _ => { });
            Check(progress.SetPaused(false, true));
            progress.Report(TransferStatus.Transferring, 400);
            Check(item.Status == TransferStatus.RemotePaused && item.CompletedBytes == 400 && item.IsActive && !item.CanResume);
            var wait = progress.WaitAsync(CancellationToken.None);
            Check(!wait.IsCompleted);
            Check(progress.SetPaused(true, true));
            Check(progress.SetPaused(false, false));
            Check(item.Status == TransferStatus.Paused && !wait.IsCompleted);
            Check(progress.SetPaused(true, false));
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Check(item.Status == TransferStatus.Resuming);
            progress.SetPaused(true, true); progress.SetPaused(false, true); progress.SetPaused(true, false);
            Check(item.Status == TransferStatus.RemotePaused);
            progress.SetPaused(false, false);
            foreach (var status in new[] { TransferStatus.Verifying, TransferStatus.Committing, TransferStatus.Completed })
            {
                progress.Report(status);
                Check(!progress.SetPaused(false, true));
                progress.Report(TransferStatus.Transferring, 500);
                Check(item.Status == status);
            }
        }
        Console.WriteLine($"BlueLink transfer pause verification passed: {checks} checks");
    }
}
