using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class SettingsPage
{
    // Compare values, not change events: reverting a control to its saved value is clean.
    internal bool HasUnsavedChanges => !_isInitializing && !_savedDraft.SequenceEqual(CaptureDraft());
    private string[] CaptureDraft() => [
        ThemeBox.SelectedValue?.ToString() ?? "", LanguageBox.SelectedValue?.ToString() ?? "",
        SendShortcutBox.SelectedValue?.ToString() ?? "",
        CloseBehaviorBox.SelectedValue?.ToString() ?? "", DuplicatePolicyBox.SelectedValue?.ToString() ?? "",
        RetentionBox.SelectedValue?.ToString() ?? "", DownloadPathText.Text.Trim(), ReceiveLimitText.Text.Trim(),
        ConnectionUsbToggle.IsChecked.ToString()!, ScanStartupToggle.IsChecked.ToString()!, AutoConnectToggle.IsChecked.ToString()!,
        AutoDownloadToggle.IsChecked.ToString()!, DiscoveryToggle.IsChecked.ToString()!, ReconnectToggle.IsChecked.ToString()!,
        MessageNotificationsToggle.IsChecked.ToString()!, ConnectionNotificationsToggle.IsChecked.ToString()!,
        TransferNotificationsToggle.IsChecked.ToString()!, ReceiveLimitToggle.IsChecked.ToString()!, ThumbnailsToggle.IsChecked.ToString()!,
        SaveChatToggle.IsChecked.ToString()!, SaveTransfersToggle.IsChecked.ToString()!, DiagnosticsToggle.IsChecked.ToString()!
    ];

    internal bool ConfirmLeave()
    {
        if (!CanLeave) return false;
        if (!HasUnsavedChanges) return true;
        _isConfirmingLeave = true;
        try
        {
            var choice = BlueLinkDialog.AskSaveSettings(HostWindow);
            if (choice == MessageBoxResult.Secondary) return true;
            if (choice != MessageBoxResult.Primary) return false;
            // Keep the existing synchronous navigation contract while dispatching asynchronous save work.
            // Re-entrant navigation is blocked until validation and persistence have both finished.
            var save = SaveDraftAsync();
            if (!save.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = save.ContinueWith(_ => Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            return save.GetAwaiter().GetResult();
        }
        finally { _isConfirmingLeave = false; }
    }
}
