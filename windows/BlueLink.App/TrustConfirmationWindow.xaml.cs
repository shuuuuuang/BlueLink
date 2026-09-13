using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BlueLink.Security;

namespace BlueLink;

public partial class TrustConfirmationWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TrustRequest _request;
    private bool _closed;
    public bool RetryRequested { get; private set; }
    public bool ManageTrustRequested { get; private set; }

    public TrustConfirmationWindow(TrustRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request;
        request.PropertyChanged += RequestChanged;
        Closed += (_, _) => { _closed = true; request.PropertyChanged -= RequestChanged; };
        ApplyState();
        Loaded += (_, _) => TrustCloseButton.Focus();
    }

    private void RequestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ApplyState));
    }

    private void ApplyState()
    {
        if (_closed) return;
        TrustPrimaryButton.Opacity = _request.PrimaryEnabled ? 1 : .5;
        TrustStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _request.Stage is TrustStage.TimedOut or TrustStage.IdentityChanged or TrustStage.Revoked or TrustStage.Failed ? "DangerBrush" : _request.Stage == TrustStage.Rejected ? "MutedBrush" : "BlueBrush");
        SecondFingerprintLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _request.Stage == TrustStage.IdentityChanged ? "DangerBrush" : "MutedBrush");
        if (_request.Stage is TrustStage.Completed or TrustStage.Canceled && IsLoaded) Close();
    }

    private void TrustContent_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Fractional pixels at 125/150% DPI must not create a scrollbar for sub-DIP overflow.
        var scroll = (System.Windows.Controls.ScrollViewer)sender;
        var visibility = scroll.ScrollableHeight > 1
            ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Hidden;
        if (scroll.VerticalScrollBarVisibility != visibility) scroll.VerticalScrollBarVisibility = visibility;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        if (Owner is { } owner) MaxHeight = Math.Max(360, owner.ActualHeight - 24);
        base.OnSourceInitialized(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_request.Stage == TrustStage.Confirm) { _request.Confirm(); return; }
        if (!_request.PrimaryEnabled) return;
        ManageTrustRequested = _request.Stage == TrustStage.IdentityChanged;
        RetryRequested = !ManageTrustRequested;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e) => _request.Cancel();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
