using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace BlueLink;

public partial class MainWindow
{
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (DevicesColumn is null) return;
        DevicesColumn.Width = new(Math.Clamp(264 + (args.NewSize.Width - 1000) * .18, 264, 340));
        ApplyComposerHeight();
    }

    private void DeviceGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        switch (element.Tag)
        {
            case "Connected": _model.ConnectedExpanded = !_model.ConnectedExpanded; break;
            case "Offline": _model.OfflineExpanded = !_model.OfflineExpanded; break;
            case "Nearby": _model.NearbyExpanded = !_model.NearbyExpanded; break;
        }
    }

    private void ShowGlobalFiles() => OpenFileWorkspace(allDevices: true);
    internal void OpenFileWorkspace(bool allDevices)
    {
        if (!TryCloseSettings()) return;
        _model.ShowFiles = true;
        // Checked can invoke FilesView_Click. Apply the requested scope after that callback.
        FilesViewButton.IsChecked = true;
        _fileDevice = _fileScope = allDevices || !_model.HasActiveConversation ? "" : "@current";
        _fileColumnSelections["Route"].RemoveWhere(key => key.StartsWith("peer:"));
        ConfigureTransferView();
    }

    private void MessagesView_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _model.ShowFiles = false;
        AttachMessageScrollViewer();
    }

    private void FilesView_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _model.ShowFiles = true;
        _fileDevice = _fileScope = _model.HasActiveConversation ? "@current" : "";
        _fileColumnSelections["Route"].RemoveWhere(key => key.StartsWith("peer:"));
        ConfigureTransferView();
    }

    private async void Home_KeyDown(object sender, KeyEventArgs e)
    {
        if (_model.IsSettingsOpen) return;
        if (_model.MessageSelectionMode)
        {
            if (e.Key == Key.Escape) { e.Handled = true; if (!_messageBatchRunning) SetMessageSelectionMode(false); return; }
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; return; }
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; MessageBatchCopy_Click(sender, e); return; }
        }
        if (_model.FileSelectionMode)
        {
            if (e.Key == Key.Escape) { e.Handled = true; SetFileSelectionMode(false); return; }
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; return; }
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (FileBatchCopy.IsEnabled) FileBatch_Click(FileBatchCopy, e); return; }
        }
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat) await _model.ScanAsync();
            return;
        }
        if (e.Key != Key.F || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ShowGlobalFiles();
        else { DeviceSearchInput.Focus(); DeviceSearchInput.SelectAll(); }
        e.Handled = true;
    }

    private void BluetoothSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception failure) { BlueLinkDialog.Show(this, "无法打开蓝牙设置", failure.Message); }
    }
}
