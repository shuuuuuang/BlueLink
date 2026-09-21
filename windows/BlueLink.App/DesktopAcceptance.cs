using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BlueLink.Security;
using BlueLink.Storage;
using BlueLink.Domain;
using BlueLink.Session;

namespace BlueLink;

/// <summary>Explicit desktop acceptance mode: isolated durable fixtures; no transport startup.</summary>
internal static partial class DesktopAcceptance
{
    internal static readonly HashSet<string> Scenes = new(StringComparer.Ordinal)
    {
        "settings-trusted", "tray-preview", "message-arrival",
        "security-confirm", "security-waiting", "security-rejected", "security-timeout", "security-identity-changed",
        "toast-info", "toast-success", "toast-warning", "toast-error", "toast-stacked",
        "first-use", "zero-connected", "bluetooth-off", "connected", "connected-empty", "offline-empty", "nearby-empty",
        "refresh-scanning", "refresh-complete", "refresh-interactive", "connect-loading", "connect-failed", "connect-success",
        "message-empty", "message-statuses", "drop-send", "drop-blocked", "drop-composer", "drop-composer-limit",
        "files-stage-progress", "files-device-transfer", "files-device-transfer-paused", "files-current", "files-global", "files-offline", "files-bluetooth-off", "message-history", "search-results", "search-empty"
    };

    static DesktopAcceptance()
    {
        Scenes.UnionWith(MenuFixtures.Keys);
        Scenes.UnionWith(FeedbackScenes);
        Scenes.UnionWith(UpdateScenes);
        Scenes.UnionWith(UsbScenes);
    }

    public static async Task InitializeAsync(MainWindow window, string directory, string? scene = null)
    {
        if (!File.Exists(Path.Combine(directory, "bluelink.db")))
        {
            var db = new BlueLinkDatabase(directory, directory);
            await db.InitializeAsync(new IdentityStore(directory));
            var download = Path.Combine(directory, "Download");
            Directory.CreateDirectory(download);
            await db.SaveSettingsAsync(BlueLinkSettings.Defaults(download) with
            { ScanOnStartup = false, AutoConnectTrustedDevices = false, KeepBackgroundSessions = false,
                LocalDeviceName = "QA Windows 桌面验收", Theme = "light" });
            var now = DateTimeOffset.Now;
            for (var peerIndex = 1; peerIndex <= 3; peerIndex++)
            {
                var id = peerIndex.ToString("X32");
                var conversationId = $"peer:{id.ToLowerInvariant()}";
                var fileFixture = HasFileFixture(scene);
                var peerName = fileFixture ? new[] { "QA REDMI K80 Pro", "QA SURFACE-LAPTOP", "QA OFFICE-PC" }[peerIndex - 1] : $"QA 离线设备 {peerIndex}";
                await db.UpsertPeerAsync(new(id, peerName, fileFixture && peerIndex > 1 ? "Windows" : "Android", StoredTrustState.Unknown,
                    null, now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), now.AddHours(-peerIndex).ToUnixTimeMilliseconds()));
                await db.UpsertConversationAsync(new(conversationId, id, now.ToUnixTimeMilliseconds(), fileFixture ? peerIndex == 1 ? 3 : 0 : peerIndex));
                if (fileFixture) continue;
                if (peerIndex == 1 && scene is "message-empty" or "message-statuses")
                {
                    if (scene == "message-statuses")
                    {
                        var examples = new (MessageStatus Status, string Text)[]
                        {
                            (MessageStatus.LocalQueued, "稍后发你最终确认稿。"),
                            (MessageStatus.Sending, "正在发送测试消息…"),
                            (MessageStatus.Failed, "这条消息未能发出。"),
                            (MessageStatus.Delivered, "需求文档已经更新。"),
                            (MessageStatus.Read, "收到后告诉我你的意见。")
                        };
                        for (var index = 0; index < examples.Length; index++)
                            await db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), conversationId, id,
                                StoredMessageDirection.Outgoing, StoredMessageType.Text, examples[index].Text,
                                examples[index].Status.ToString(), now.ToUnixTimeMilliseconds(), index));
                    }
                    continue;
                }
                for (var index = 0; index < 18; index++)
                {
                    await db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), conversationId, id,
                        index % 2 == 0 ? StoredMessageDirection.Incoming : StoredMessageDirection.Outgoing,
                        StoredMessageType.Text, $"QA 桌面验收记录 {index + 1}：用于检查搜索、滚动、窄窗口和页面切换。",
                        index % 2 == 0 ? "Received" : "Delivered", now.AddMinutes(index - 20).ToUnixTimeMilliseconds(), index));
                }
            }
            if (HasFileFixture(scene)) await SeedFileFixtureAsync(db, download, scene!, now);
        }
        await window.ViewModel.InitializeLocalStateAsync();
        if (scene is not null)
        {
            await window.ViewModel.ApplyDesktopAcceptanceSceneAsync(scene);
            if (scene.StartsWith("files-", StringComparison.Ordinal)) window.OpenFileWorkspace(scene == "files-global");
            if (scene is "search-results" or "search-empty" && new WindowInteropHelper(window).Handle != IntPtr.Zero)
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    window.ShowMessageSearch(scene == "search-results" ? "需求文档" : "安装手册")));
            if (scene is "drop-send" or "drop-blocked") window.ShowFileDropFeedback(1);
            if (scene is "drop-composer" or "drop-composer-limit") window.ShowFileDropFeedback(scene == "drop-composer" ? 1 : 11, composer: true);
            if (scene.StartsWith("security-", StringComparison.Ordinal) && new WindowInteropHelper(window).Handle != IntPtr.Zero)
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ShowSecurityFixture(window, directory, scene)));
            if (scene.StartsWith("toast-", StringComparison.Ordinal)) ApplyToastFixture(window, scene);
            if (scene == "settings-trusted") window.OpenSettings(connections: true);
            if (FeedbackScenes.Contains(scene)) await ApplyFeedbackFixtureAsync(window, directory, scene);
            if (UpdateScenes.Contains(scene)) await ApplyUpdateFixtureAsync(window, directory, scene);
            if (UsbScenes.Contains(scene)) ApplyUsbFixture(window, scene);
            if (scene == "tray-preview") ShowTrayFixture(window);
            if (scene == "message-arrival") ScheduleIncomingMessageFixture(window);
            if (scene == "refresh-interactive") EnableRefreshFixture(window.ViewModel, directory);
            File.WriteAllText(Path.Combine(directory, "desktop-scene.json"), JsonSerializer.Serialize(new
            {
                scene, visualFixture = true, transportStarted = false,
                note = "Isolated visual state only; no Bluetooth/USB connection is established."
            }));
        }
    }

    public static void ObserveGeometry(MainWindow window, string directory)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        void Save()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var restore = window.RestoreBounds;
                File.WriteAllText(Path.Combine(directory, "desktop-geometry.json"), JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now, window.ActualWidth, window.ActualHeight,
                    window.MinWidth, window.MinHeight, state = window.WindowState.ToString(),
                    restoreWidth = restore.IsEmpty ? 0 : restore.Width, restoreHeight = restore.IsEmpty ? 0 : restore.Height,
                    dpi.DpiScaleX, dpi.DpiScaleY, window.Left, window.Top,
                    hwnd = new WindowInteropHelper(window).Handle.ToInt64(),
                    nativeWindowCount = Application.Current.Windows.Cast<Window>().Count(value => new WindowInteropHelper(value).Handle != IntPtr.Zero),
                    settingsOpen = window.ViewModel.IsSettingsOpen,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
        }
        timer.Tick += (_, _) => { timer.Stop(); Save(); };
        window.SizeChanged += (_, _) => { timer.Stop(); timer.Start(); };
        window.StateChanged += (_, _) => { timer.Stop(); timer.Start(); };
        window.ContentRendered += (_, _) => Save();
        window.Closing += (_, _) => Save();
        window.Closed += (_, _) => timer.Stop();
    }
}

public sealed partial class MainViewModel
{
    // Only called by the explicit isolated desktop acceptance startup path.
    internal async Task ApplyDesktopAcceptanceSceneAsync(string scene)
    {
        if (!DesktopAcceptance.Scenes.Contains(scene)) throw new ArgumentOutOfRangeException(nameof(scene));
        Sessions.Clear(); Devices.Clear();
        _bluetoothEnabled = scene is not ("bluetooth-off" or "files-bluetooth-off");
        _bluetoothRadioPresent = true;
        var peers = _storedPeers.Values.OrderBy(peer => peer.PeerId).ToArray();
        if (scene == "settings-trusted" && _identity.TrustedIdentities.Count == 0)
            foreach (var peer in peers.Take(2)) _identity.Trust(Convert.FromHexString(peer.PeerId), DeviceIdentity.Generate().PublicKey);
        var now = DateTimeOffset.Now;
        if (scene == "first-use") _storedPeers.Clear();
        else
        {
            var connected = scene is not ("zero-connected" or "bluetooth-off" or "connected-empty" or "drop-blocked" or "files-offline" or "files-bluetooth-off");
            if (connected)
                foreach (var peer in peers.Take(2))
                    Sessions.Add(new(Guid.NewGuid(), peer.PeerId, peer.DisplayName, peer.TransportAddress,
                        ConnectionPhase.Connected, "端到端加密 · BTX/1.1", now));
            if (scene == "offline-empty")
                foreach (var peer in peers.Skip(2)) _storedPeers.Remove(peer.PeerId);
            if (scene is not ("bluetooth-off" or "files-bluetooth-off" or "nearby-empty" or "refresh-interactive"))
                Devices.Add(new("qa-nearby", "QA Galaxy S24", "00:00:00:00:00:04", PeerPlatform.Android, -58, now, true));
        }
        if (scene == "connect-success")
        {
            const string id = "00000000000000000000000000000004";
            _storedPeers[id] = new(id, "QA Galaxy S24", "Android", StoredTrustState.Unknown, null,
                now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), "00:00:00:00:00:04");
            Sessions.Add(new(Guid.NewGuid(), id, "QA Galaxy S24", "00:00:00:00:00:04", ConnectionPhase.Connected,
                "端到端加密 · BTX/1.1", now));
            Devices.Clear();
        }
        if (scene.StartsWith("files-", StringComparison.Ordinal)) ShowFiles = true;
        RefreshConversations();
        RaiseBluetoothStatus();
        if (scene is not ("first-use" or "files-global")) await SelectConversationAsync(peers[0].PeerId);
        if (_activeSessionId is { } sessionId) await LoadHistoryAsync(peers[0].PeerId, sessionId);
        if (scene == "files-stage-progress")
        {
            var session = Sessions.First(value => value.PeerId == peers[0].PeerId);
            var stages = AllTransfers.Where(value => value.PeerId == peers[0].PeerId && !value.Outgoing && value.LocalPath is null)
                .OrderBy(value => value.CreatedAt).ToArray();
            for (var index = 0; index < stages.Length; index++)
            {
                var transfer = stages[index].Snapshot();
                transfer.Status = index == 0 ? TransferStatus.Committing : TransferStatus.Verifying;
                transfer.CompletedBytes = transfer.TotalBytes;
                transfer.FailureDetail = null;
                OnSessionTransfer(session, transfer);
            }
            OnSessionTransfer(session, new TransferItem { Id = Guid.NewGuid(), Name = "QA 等待发送.zip", TotalBytes = 4096,
                Outgoing = true, PeerId = session.PeerId, Status = TransferStatus.Queued, QueuedForUsb = true, LocalPath = Path.Combine(DataDirectory, "QA-source.zip") });
        }
        if (scene == "refresh-scanning")
        {
            IsScanning = true;
            ScanFeedback = "正在扫描附近运行蓝联的设备…";
        }
        if (scene == "refresh-complete")
        {
            _scanCompleted = true;
            ScanFeedback = "扫描完成，发现 1 台新设备";
        }
        if (scene is "connect-loading" or "connect-failed" or "connect-success")
            UpdateConnectionAttempt("00:00:00:00:00:04",
                scene == "connect-loading" ? ConnectionPhase.Connecting : scene == "connect-failed" ? ConnectionPhase.Disconnected : ConnectionPhase.Connected,
                scene == "connect-failed" ? "连接超时，请确认对方已打开蓝联" : "");
    }
}
