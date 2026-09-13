using BlueLink.Bluetooth;
using System.IO;

internal static class BluetoothCallbackVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks++;
        }
        var timeout = TimeSpan.FromSeconds(3);

        // A real socket must reach the handshake while the independent GATT call is still pending.
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new CallbackConnectionRequest<Socket>();
        var waiting = pending.WaitAsync(_ => dispatch.Task, timeout, CancellationToken.None);
        var socket = new Socket();
        Check(pending.TryAccept(socket), "incoming socket accepted during dispatch");
        Check(ReferenceEquals(await waiting.WaitAsync(timeout), socket) && socket.Closed == 0,
            "socket reaches its owner without waiting for GATT completion");
        dispatch.SetException(new IOException("GATT AccessDenied after RFCOMM arrived"));
        Check(!pending.TryAccept(new Socket()), "second incoming socket stays with listener");
        await socket.DisposeAsync();

        pending = new();
        try
        {
            await pending.WaitAsync(_ => Task.FromException(new IOException("GATT failed")), timeout, CancellationToken.None);
            throw new InvalidOperationException("dispatch failure was lost");
        }
        catch (IOException) { checks++; }
        Check(!pending.TryAccept(new Socket()), "late socket after failure uses normal incoming path");

        pending = new();
        using var canceled = new CancellationTokenSource();
        waiting = pending.WaitAsync(_ => Task.CompletedTask, timeout, canceled.Token);
        canceled.Cancel();
        try { await waiting; throw new InvalidOperationException("cancel did not terminate wait"); }
        catch (OperationCanceledException) { checks++; }
        Check(!pending.TryAccept(new Socket()), "canceled request cannot capture a socket");

        pending = new();
        try
        {
            await pending.WaitAsync(_ => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None);
            throw new InvalidOperationException("callback deadline did not expire");
        }
        catch (TimeoutException) { checks++; }
        Check(!pending.TryAccept(new Socket()), "expired callback request cannot capture a socket");

        // Deterministic ownership assertions under genuinely concurrent failure / arrival ordering.
        for (var i = 0; i < 32; i++)
        {
            pending = new();
            dispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            socket = new();
            waiting = pending.WaitAsync(_ => dispatch.Task, timeout, CancellationToken.None);
            using var start = new ManualResetEventSlim();
            var arriving = Task.Run(() => { start.Wait(); return pending.TryAccept(socket); });
            var failing = Task.Run(() => { start.Wait(); dispatch.TrySetException(new IOException("racing failure")); });
            start.Set();
            var accepted = await arriving;
            await failing;
            try
            {
                var result = await waiting;
                Check(accepted && ReferenceEquals(result, socket), "accepted connection survives concurrent dispatch failure");
            }
            catch (IOException) { Check(!accepted, "failed request leaves connection ownership with listener"); }
            Check(socket.Closed == 0, "connection remains available to exactly one caller");
            await socket.DisposeAsync();
        }
        Console.WriteLine($"Bluetooth callback ownership verification passed: {checks} checks (no radio or system mutations).");
    }

    private sealed class Socket : IAsyncDisposable
    {
        public int Closed;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Closed); return ValueTask.CompletedTask; }
    }
}
