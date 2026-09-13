using System.Diagnostics;
using BlueLink.Usb;

internal static class MtpResponsivenessVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        long now = 100;
        var signal = new MtpProbeSignal(() => now);
        Check(signal.DelayMilliseconds(true, false) == 3000, "idle backoff");
        signal.Request();
        Check(signal.DelayMilliseconds(true, false) == 250, "short retry after change");
        signal.BackOffBusyInterface();
        Check(signal.DelayMilliseconds(true, false) == 3000, "busy interfaces must not be hammered");
        signal.Request();
        Check(signal.DelayMilliseconds(true, false) == 250, "new physical change resets busy backoff");
        Check(signal.DelayMilliseconds(true, true) == 3000, "ready heartbeat is not accelerated");
        Check(signal.DelayMilliseconds(false, false) == 30000, "disabled is quiet");
        await signal.WaitAsync(false, false, default).WaitAsync(TimeSpan.FromSeconds(1));
        checks++;
        now += 10001;
        Check(signal.DelayMilliseconds(true, false) == 3000, "fast retries expire");
        signal.Request(fastRetry: false);
        Check(signal.DelayMilliseconds(true, false) == 3000, "periodic announcements cannot extend burst");
        await signal.WaitAsync(false, false, default).WaitAsync(TimeSpan.FromSeconds(1));
        var waiting = signal.WaitAsync(false, false, default);
        await Task.Delay(30);
        var timer = Stopwatch.StartNew(); signal.Request();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Check(timer.ElapsedMilliseconds < 1000, "device event interrupts existing wait");
        for (var i = 0; i < 1000; i++) signal.Request();
        await signal.WaitAsync(false, false, default).WaitAsync(TimeSpan.FromSeconds(1));
        using var stop = new CancellationTokenSource(80);
        try { await signal.WaitAsync(false, false, stop.Token); throw new Exception("Queued duplicate wakes or swallowed cancellation"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { checks++; }
        Console.WriteLine($"MTP responsiveness: {checks} checks passed");
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    }
}

