using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BlueLink.Updates;

namespace BlueLink;

public partial class UpdateWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateWorkflow _workflow;
    private readonly Func<bool> _canInstall;
    private bool _closed;
    public UpdateWindow(UpdateWorkflow workflow, Func<bool> canInstall)
    {
        _workflow = workflow; _canInstall = canInstall;
        InitializeComponent(); DataContext = workflow;
        workflow.PropertyChanged += Changed;
        Closed += (_, _) => { _closed = true; workflow.PropertyChanged -= Changed; };
        ApplyState();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ApplyState));
    }
    private void ApplyState()
    {
        if (_closed) return;
        UpdatePrimaryButton.IsEnabled = !_workflow.Busy;
        UpdateNote.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, _workflow.HasError ? "SoftDangerBrush" : "SoftBlueBrush");
        UpdateNoteTitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _workflow.HasError ? "DangerBrush" : "InkBrush");
    }
    private async void Primary_Click(object sender, RoutedEventArgs args)
    {
        if (_workflow.Busy) return;
        if (_workflow.Stage == UpdateStage.Ready)
        {
            if (await _workflow.InstallAsync(_canInstall) && Application.Current is App app) await app.RequestExitAsync();
        }
        else await _workflow.DownloadAsync();
    }
    private void Close_Click(object sender, RoutedEventArgs args) => Close();
    private void Window_Closing(object? sender, CancelEventArgs args) => _workflow.Cancel();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }
}
