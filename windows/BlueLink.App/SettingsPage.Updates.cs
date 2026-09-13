using System.ComponentModel;
using System.Windows;
using BlueLink.Updates;

namespace BlueLink;

public partial class SettingsPage
{
    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        var updates = _model.Updates;
        if (updates.Busy) return;
        if (updates.Stage is not (UpdateStage.Available or UpdateStage.DownloadFailed or UpdateStage.Ready)) await updates.CheckAsync();
        if (_updatesClosed) return;
        if (updates.Stage is UpdateStage.Available or UpdateStage.DownloadFailed or UpdateStage.Ready)
        {
            var window = new UpdateWindow(updates, () => _model.CanInstallUpdate) { Owner = HostWindow };
            using var dim = BlueLinkDialog.DimOwner(HostWindow, "#4D0D1729", 0);
            window.ShowDialog();
        }
    }
    private bool _updatesClosed;
    private void ObserveUpdates()
    {
        _model.Updates.PropertyChanged += UpdateChanged;
        Disposed += (_, _) => { _updatesClosed = true; _model.Updates.PropertyChanged -= UpdateChanged; _model.Updates.Cancel(); };
        ApplyUpdateStatus();
    }
    private void UpdateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_updatesClosed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ApplyUpdateStatus));
    }
    private void ApplyUpdateStatus()
    {
        if (_updatesClosed) return;
        CheckUpdateButton.IsEnabled = !_model.Updates.Busy;
        UpdateStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _model.Updates.Stage == UpdateStage.CheckFailed ? "DangerBrush" : "BlueBrush");
        CheckUpdateButton.ToolTip = string.IsNullOrEmpty(_model.Updates.Error) ? null : _model.Updates.Error;
    }
}
