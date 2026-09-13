namespace BlueLink;

public partial class MainWindow
{
    private void OnTransientNoticeRequested(string message, ToastLevel level)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnTransientNoticeRequested(message, level)));
            return;
        }
        if (!_disposed) ShowToast(message, level);
    }

    internal void ShowToast(string message, ToastLevel level = ToastLevel.Info) => Toasts.Show(message, level);

    private async Task WithToastAsync(Func<Task> operation, string success, string failure, ToastLevel level = ToastLevel.Success)
    {
        try
        {
            await operation();
            if (!_disposed) ShowToast(success, level);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("BlueLink local operation failed: {0}", error);
            if (!_disposed) ShowToast(failure, ToastLevel.Error);
        }
    }
}
