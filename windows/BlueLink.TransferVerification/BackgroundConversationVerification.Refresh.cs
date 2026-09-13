using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlueLink;
using BlueLink.Domain;

internal static partial class BackgroundConversationVerification
{
    private static void VerifyNearbyRefresh(MainWindow window, Action<bool, string> check)
    {
        var model = window.ViewModel;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetField("_bluetoothEnabled", flags)!.SetValue(model, true);
        typeof(MainViewModel).GetField("_bluetoothRadioPresent", flags)!.SetValue(model, true);
        typeof(MainViewModel).GetMethod("RaiseBluetoothStatus", flags)!.Invoke(model, null);
        var sidebar = (Border)window.FindName("DevicesSidebar");
        var menu = sidebar.ContextMenu;
        menu.PlacementTarget = sidebar;
        var refresh = (MenuItem)menu.Items[0];
        var completion = new TaskCompletionSource<IReadOnlyList<NearbyDevice>>();
        var starts = 0;
        model.ScanForAcceptance = () => { starts++; return completion.Task; };
        void F5() => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window), Environment.TickCount, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        try
        {
            Drain();
            check(window.FindName("ScanButton") is null && refresh.IsEnabled && refresh.InputGestureText == "F5",
                "nearby refresh is available in the menu with F5 and no permanent button");
            var activePeer = model.ActivePeerId;
            var messageIds = model.Messages.Select(value => value.Id).ToArray();
            model.NearbyExpanded = false;
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain();
            check(starts == 1 && model.IsScanning && !refresh.IsEnabled && !model.NearbyExpanded,
                "menu starts one scan, disables refresh, and preserves collapsed group");
            for (var index = 0; index < 5; index++) F5();
            Wait(model.ScanAsync());
            check(starts == 1, "repeated F5 and refresh requests reuse the active scan without restarting it");
            completion.SetResult(Array.Empty<NearbyDevice>());
            Until(() => !model.IsScanning); Drain();
            check(refresh.IsEnabled && model.ActivePeerId == activePeer && model.Messages.Select(value => value.Id).SequenceEqual(messageIds),
                "scan completion restores refresh without changing the selected conversation or history");
            check(((StackPanel)window.FindName("NearbyScanStatus")).Visibility == Visibility.Collapsed &&
                window.FindName("NearbyRefreshHint") is null, "completion hides progress without an extra empty-state hint");
            completion = new TaskCompletionSource<IReadOnlyList<NearbyDevice>>();
            F5(); Drain();
            check(starts == 2 && model.IsScanning, "F5 starts a fresh scan after the preceding one completed");
            completion.SetResult(new[] { new NearbyDevice("refresh-result", "QA refreshed device", "00:00:00:00:00:05", PeerPlatform.Android,
                LastSeen: DateTimeOffset.UtcNow, CanInitiate: true) });
            Until(() => !model.IsScanning); Drain();
            check(model.NearbyNewDevices.Any(device => device.Name == "QA refreshed device"), "F5 discovery results populate the actual nearby list");
            window.OpenSettings(); Drain();
            F5(); Drain();
            check(starts == 2, "F5 in settings does not trigger a device scan");
            window.TryCloseSettings(); Drain();
            typeof(MainViewModel).GetField("_bluetoothEnabled", flags)!.SetValue(model, false);
            typeof(MainViewModel).GetMethod("RaiseBluetoothStatus", flags)!.Invoke(model, null); Drain();
            F5();
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain();
            check(!refresh.IsEnabled && starts == 2, "Bluetooth unavailable blocks menu and keyboard refresh");
        }
        finally
        {
            completion.TrySetResult(Array.Empty<NearbyDevice>());
            model.ScanForAcceptance = null;
        }
    }
}
