using BlueLink.Usb;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal static readonly string[] UsbScenes = ["usb-off", "usb-enabled"];
    private static void ApplyUsbFixture(MainWindow window, string scene)
    {
        window.ViewModel.ApplyUsbAcceptanceState(scene == "usb-enabled");
        window.OpenSettings();
        window.ActiveSettingsPage!.ShowConnections();
    }
}

public sealed partial class MainViewModel
{
    internal void ApplyUsbAcceptanceState(bool enabled)
    {
        if (_runtimeStarted) throw new InvalidOperationException("USB visual fixtures require an isolated runtime.");
        _settings = _settings with { UsbEnabled = enabled };
        RefreshConversations();
        Raise(nameof(Settings));
        Raise(nameof(ActiveUsbReady));
    }
}
