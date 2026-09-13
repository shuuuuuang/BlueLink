namespace BlueLink.Bluetooth;

/// <summary>Transfers exactly one incoming socket, even when GATT dispatch fails concurrently.</summary>
public sealed class CallbackConnectionRequest<T> where T : class, IAsyncDisposable
{
    private readonly TaskCompletionSource<T> _connection = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A rejected connection remains owned by the listener and can use normal inbound handling.
    public bool TryAccept(T connection) => _connection.TrySetResult(connection);
    public void Cancel() => _connection.TrySetCanceled();

    public async Task<T> WaitAsync(Func<CancellationToken, Task> sendRequest, TimeSpan timeout,
        CancellationToken token)
    {
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(token);
        var transferred = false;
        try
        {
            _ = DispatchAsync(sendRequest, dispatch.Token);
            var connection = await _connection.Task.WaitAsync(timeout, token);
            transferred = true;
            return connection;
        }
        finally
        {
            dispatch.Cancel();
            // Close an accepted socket if cancellation won before it reached the session owner.
            if (!transferred && !_connection.TrySetCanceled() && _connection.Task.IsCompletedSuccessfully)
                await _connection.Task.Result.DisposeAsync();
        }
    }

    private async Task DispatchAsync(Func<CancellationToken, Task> sendRequest, CancellationToken token)
    {
        try { await sendRequest(token); }
        catch (OperationCanceledException) { _connection.TrySetCanceled(); }
        catch (Exception failure) { _connection.TrySetException(failure); }
        // Successful dispatch still waits for a socket; an earlier socket wins over a late error.
    }
}
