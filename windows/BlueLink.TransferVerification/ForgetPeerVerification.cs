using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Storage;
using BlueLink.Transport;

internal static class ForgetPeerVerification
{
    public static void Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            SessionSupervisor? remote = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var root = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N"));
                var identity = new IdentityStore(root);
                var other = new IdentityStore(Path.Combine(root, "remote"));
                var offline = DeviceIdentity.Generate();
                identity.Trust(other.Identity.PeerId, other.Identity.PublicKey);
                identity.Trust(offline.PeerId, offline.PublicKey);
                other.Trust(identity.Identity.PeerId, identity.Identity.PublicKey);
                var peerId = Convert.ToHexString(other.Identity.PeerId);
                var offlineId = Convert.ToHexString(offline.PeerId);
                var db = new BlueLinkDatabase(root, root);
                Wait(db.InitializeAsync(identity));
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var (id, key, address) in new[] { (peerId, other.Identity.PublicKey, "AA:BB:CC:DD:EE:01"), (offlineId, offline.PublicKey, "AA:BB:CC:DD:EE:02") })
                {
                    Wait(db.UpsertPeerAsync(new(id, "QA Android", "Android", StoredTrustState.Trusted, key, now, now, now, address)));
                    Wait(db.UpsertConversationAsync(new("peer:" + id.ToLowerInvariant(), id, now, 2, "保留草稿")));
                    Wait(db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), "peer:" + id.ToLowerInvariant(), id, StoredMessageDirection.Incoming, StoredMessageType.Text, "保留历史", "Received", now, 1)));
                }
                var keptFile = Path.Combine(root, "received.txt");
                File.WriteAllText(keptFile, "retained file");
                Wait(db.UpsertTransferAsync(new(Guid.NewGuid().ToString("N"), peerId, null, "Incoming", "Completed", "received.txt", "text/plain", 13, 13, keptFile, null, null, null, null, now, now)));
                window = new MainWindow(initializeRuntime: false, dataRoot: root)
                {
                    Width = 1180, Height = 720, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                };
                var model = window.ViewModel;
                Wait(model.InitializeLocalStateAsync());
                Wait(model.SaveSettingsAsync(model.Settings with { Theme = "light", AutoConnectTrustedDevices = false, ReconnectAfterDisconnect = false, ScanOnStartup = false }));
                window.Show(); Drain();
                var host = Field<SessionSupervisor>(model, "_sessions");
                var hostIdentity = Field<IdentityStore>(model, "_identity");
                remote = new SessionSupervisor(other, r => r.Confirm()) { LocalDeviceName = "QA Android" };
                Connect();
                Until(() => model.ConnectedConversations.Any(p => p.PeerId == peerId));
                Discover(new NearbyDevice("qa-discovery", "QA Android", "AA:BB:CC:DD:EE:01", PeerPlatform.Android));
                Check(model.OfflineConversations.Count == 1 && model.NearbyNewDevices.Count == 0, "connected and offline trusted peers suppress duplicate discoveries");
                Wait(model.ForgetPeerAsync(peerId));
                Until(() => host.ActiveCount == 0 && remote.ActiveCount == 0 && model.NearbyNewDevices.Count == 1);
                Check(model.ConnectedConversations.Count == 0 && model.OfflineConversations.Single().PeerId == offlineId,
                    "forget connected peer disconnects encrypted session and moves only discovered endpoint to nearby");
                Wait(model.ForgetPeerAsync(offlineId));
                Check(model.Conversations.Count == 0 && model.NearbyNewDevices.Count == 1, "forget offline peer disappears without fabricating discovery");
                Capture(window, "removed-discovered");
                Discover();
                Check(model.NearbyNewDevices.Count == 0, "expired discovery removes the new-device card too");
                Discover(new NearbyDevice("qa-discovery", "QA Android", "AA:BB:CC:DD:EE:01", PeerPlatform.Android));
                Check(model.NearbyNewDevices.Count == 1, "new scan can rediscover a removed identity");
                var reopened = new BlueLinkDatabase(root, root);
                Wait(reopened.InitializeAsync(new IdentityStore(root)));
                Check(Wait(reopened.LoadPeersAsync()).All(p => p.TrustState == StoredTrustState.Removed), "removed state survives database and identity reload");
                Wait(model.InitializeLocalStateAsync());
                Check(model.Conversations.Count == 0 && model.NearbyNewDevices.Count == 1, "reloaded UI does not resurrect offline cards");
                foreach (var id in new[] { peerId, offlineId })
                    Check(Wait(reopened.LoadMessagesAsync("peer:" + id.ToLowerInvariant())).Single().Content == "保留历史", "history retained for " + id[..8]);
                Check(Wait(reopened.LoadTransfersAsync(peerId)).Single().LocalPath == keptFile && File.ReadAllText(keptFile) == "retained file", "transfer record and received file retained");
                hostIdentity.Trust(other.Identity.PeerId, other.Identity.PublicKey);
                Connect();
                Until(() => model.ConnectedConversations.Any(p => p.PeerId == peerId));
                Check(model.NearbyNewDevices.Count == 0 && model.Conversations.Count == 1 && Wait(db.LoadPeersAsync()).Single(p => p.PeerId == peerId).TrustState == StoredTrustState.Trusted,
                    "fresh trust and encrypted reconnect restore one known card without duplicates");
                Wait(model.SelectConversationAsync(peerId));
                Until(() => model.Messages.Any(m => m.Text == "保留历史"));
                Check(model.Messages.Any(m => m.Text == "保留历史"), "retrust restores access to original conversation history");
                Capture(window, "trusted-again");
                Wait(model.ForgetAllPeersAsync());
                Until(() => model.Conversations.Count == 0);
                Check(model.TrustedDevices.Count == 0 && model.NearbyNewDevices.Count == 1, "remove-all follows the same grouping rule");

                void Discover(params NearbyDevice[] devices)
                {
                    model.Devices.Clear(); foreach (var device in devices) model.Devices.Add(device);
                    typeof(MainViewModel).GetMethod("RefreshConversations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, null);
                    Drain();
                }
                void Connect()
                {
                    var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
                    Wait(Task.WhenAll(host.AddAsync(new Connection(left, false), false), remote.AddAsync(new Connection(right, true), true)));
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (remote is not null) Wait(remote.DisposeAsync().AsTask());
                if (window is not null) { window.Close(); Wait(window.DisposeAsync().AsTask()); }
                app?.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Forget-device verification timed out");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { checks, failure = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure is not null) throw new InvalidOperationException("Forget-device verification failed", failure);
        Console.WriteLine($"Forget-device verification passed: {checks.Count} checks; isolated identities, encrypted sessions and native UI Automation.");
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        void Capture(MainWindow window, string name)
        {
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout(); Drain();
            foreach (var id in new[] { "ConnectedDeviceList", "OfflineDeviceList", "NearbyDeviceList" })
            {
                var list = id == "NearbyDeviceList" ? Descendants(root).OfType<ListBox>().Single(value => ReferenceEquals(value.ItemsSource, window.ViewModel.NearbyDevicesView)) : (ListBox)window.FindName(id);
                var peer = new ListBoxAutomationPeer(list);
                Check((peer.GetChildren()?.Count ?? 0) == list.Items.Count, "native UI Automation reflects visible items: " + name + "/" + id);
            }
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static T Field<T>(MainViewModel model, string name) => (T)typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Condition timed out"); Drain(); Thread.Sleep(2); }
        Drain();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private sealed class Connection(Stream duplex, bool listener) : IPeerConnection
    {
        public Stream Input => duplex;
        public Stream Output => duplex;
        public string PeerName => "QA Android";
        public string TransportAddress => "AA:BB:CC:DD:EE:01";
        public bool ListenerRole => listener;
        public TransportKind Transport => TransportKind.Bluetooth;
        public PeerPlatform Platform => PeerPlatform.Android;
        public ValueTask DisposeAsync() => duplex.DisposeAsync();
    }
}
