using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Storage;

internal static class TransferPersistenceVerification
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                SessionLog.DirectoryPath = Path.Combine(output, "logs");
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var isolated = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N"));
                window = new MainWindow(false, isolated)
                {
                    Width = 1180, Height = 720, Left = -5000, Top = -5000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false, ShowInTaskbar = false
                };
                var model = window.ViewModel;
                Wait(model.InitializeLocalStateAsync());
                var database = new BlueLinkDatabase(isolated, isolated);
                var settings = typeof(MainViewModel).GetProperty(nameof(MainViewModel.Settings))!;
                var received = typeof(MainViewModel).GetMethod("OnSessionTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var persist = typeof(MainViewModel).GetMethod("PersistTransferAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var trustGate = (SemaphoreSlim)typeof(MainViewModel).GetField("_trustMutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                var sessionId = Guid.NewGuid();
                var peer = new ConversationSummary("qa-receive", "QA 接收设备", PeerPlatform.Android,
                    DeviceAvailability.Connected, sessionId, "00:00:00:00:00:01", 0, DateTimeOffset.UtcNow);
                var session = new SessionSnapshot(sessionId, peer.PeerId, peer.PeerName, peer.TransportAddress,
                    ConnectionPhase.Connected, "", DateTimeOffset.UtcNow);
                model.Sessions.Add(session);
                model.Conversations.Add(peer);
                model.ConnectedConversations.Add(peer);
                model.UseVisualFixture(peer);
                settings.SetValue(model, model.Settings with { TransferNotifications = false, SaveTransferHistory = true });
                window.Show(); Drain();

                void Check(bool valid, string name)
                {
                    if (!valid) throw new InvalidOperationException(name);
                    checks++;
                }
                void Publish(TransferItem item)
                {
                    // Control persistence separately so an old disk operation can finish after a new live event.
                    var saved = model.Settings;
                    settings.SetValue(model, saved with { SaveTransferHistory = false });
                    received.Invoke(model, [session, item]);
                    settings.SetValue(model, saved);
                    Drain();
                }
                Task Persist(TransferItem item) => (Task)persist.Invoke(model, [session, item])!;
                TransferItem Transfer(TransferStatus status) => new()
                {
                    Id = Guid.NewGuid(), Name = "received.bin", TotalBytes = 1024, Outgoing = false,
                    Status = status, CompletedBytes = 0, PeerId = peer.PeerId, SourceSha256 = new string('a', 64)
                };
                void VerifyIdle(string name)
                {
                    Check(!peer.HasActiveTransfer, name + ": device no longer reports saving");
                    var secondary = Descendants<TextBlock>(window).Single(text =>
                        text.Name == "ConversationSecondaryText" && ReferenceEquals(text.DataContext, peer));
                    var progress = Descendants<ProgressBar>(window).Single(bar =>
                        bar.Name == "ConversationTransferProgress" && ReferenceEquals(bar.DataContext, peer));
                    Check(new TextBlockAutomationPeer(secondary).GetName() == peer.LastSeenText,
                        name + ": native UI Automation reads idle device text");
                    Check(progress.Visibility == Visibility.Collapsed, name + ": device progress is collapsed");
                }

                foreach (var theme in new[] { "light", "dark" })
                {
                    AppearanceService.Apply(model.Settings with { Theme = theme, Language = "zh-CN" });
                    foreach (var terminal in new[] { TransferStatus.Completed, TransferStatus.Failed, TransferStatus.Canceled, TransferStatus.Rejected })
                    {
                        model.AllTransfers.Clear(); model.Transfers.Clear();
                        var old = Transfer(TransferStatus.Committing);
                        Publish(old);
                        Check(peer.ActiveTransfer?.Id == old.Id, "saving is visible before completion");
                        var bar = Descendants<ProgressBar>(window).Single(control =>
                            control.Name == "ConversationTransferProgress" && ReferenceEquals(control.DataContext, peer));
                        Check(bar.IsIndeterminate && new ProgressBarAutomationPeer(bar).GetPattern(PatternInterface.RangeValue) is null,
                            "UI Automation exposes saving as indeterminate, without a false numeric value");
                        Check(!peer.ActiveTransferSummary.Contains('%'), "saving summary does not claim measurable progress");
                        if (terminal == TransferStatus.Completed) Capture(window, Path.Combine(output, theme + "-saving.png"));
                        trustGate.Wait();
                        Task pending;
                        var final = old.Snapshot();
                        final.Status = terminal;
                        final.CompletedBytes = terminal == TransferStatus.Completed ? final.TotalBytes : 512;
                        final.FailureDetail = terminal == TransferStatus.Failed ? "QA failure" : null;
                        if (terminal == TransferStatus.Completed)
                        {
                            final.LocalPath = Path.Combine(isolated, "received.bin");
                            File.WriteAllBytes(final.LocalPath, new byte[1024]);
                        }
                        try
                        {
                            pending = Persist(old);
                            Check(!pending.IsCompleted, "old history write is delayed");
                            Publish(final);
                            VerifyIdle(theme + " immediate " + terminal);
                        }
                        finally { trustGate.Release(); }
                        Wait(pending); Drain();
                        Check(model.AllTransfers.Single().Status == terminal,
                            "delayed committing persistence must not overwrite " + terminal);
                        Check(model.AllTransfers.Single().CompletedBytes == final.CompletedBytes,
                            "delayed history must not reset progress");
                        VerifyIdle(theme + " delayed " + terminal);
                        Wait(Persist(final));
                        var records = Wait(database.LoadTransfersAsync(peer.PeerId));
                        var stored = records.Single(item => item.TransferId == final.Id.ToString("N"));
                        Check(stored.Status == terminal.ToString() && stored.CompletedBytes == final.CompletedBytes,
                            "terminal history survives reload");
                        Check(stored.Sha256 is { Length: 32 } hash && Convert.ToHexString(hash).Equals(final.SourceSha256, StringComparison.OrdinalIgnoreCase),
                            "source fingerprint survives progress, terminal history and reload");
                        if (terminal == TransferStatus.Completed)
                        {
                            Check(final.CanOpen, "completed received file remains available");
                            Capture(window, Path.Combine(output, theme + "-completed.png"));
                        }
                    }
                }

                // Queue snapshots while the first database operation is blocked, including retry after failure.
                model.AllTransfers.Clear(); model.Transfers.Clear();
                var queued = Transfer(TransferStatus.Transferring);
                var writes = new List<Task>();
                trustGate.Wait();
                try
                {
                    foreach (var state in new[] { TransferStatus.Transferring, TransferStatus.Failed,
                        TransferStatus.Resuming, TransferStatus.Verifying, TransferStatus.Committing, TransferStatus.Completed })
                    {
                        queued.Status = state;
                        queued.CompletedBytes = state == TransferStatus.Completed ? 1024 : 512;
                        writes.Add(Persist(queued));
                    }
                }
                finally { trustGate.Release(); }
                Wait(Task.WhenAll(writes));
                var latest = Wait(database.LoadTransfersAsync(peer.PeerId)).Single(item => item.TransferId == queued.Id.ToString("N"));
                Check(latest.Status == "Completed" && latest.CompletedBytes == 1024, "queued retry and terminal history remain ordered");

                var other = Transfer(TransferStatus.Transferring);
                Publish(other);
                var completed = Transfer(TransferStatus.Completed);
                Publish(completed);
                Check(peer.ActiveTransfer?.Id == other.Id, "completion preserves another active file indicator");
                other = other.Snapshot(); other.Status = TransferStatus.Completed;
                Publish(other);
                VerifyIdle("all transfers finished without history writes");
                File.WriteAllText(Path.Combine(output, "result.txt"), $"Passed {checks} checks; native WPF/UI Automation; 4 screenshots.");
                Console.WriteLine($"Transfer persistence verification passed: {checks} checks; 4 screenshots.");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (window is not null) { Wait(window.DisposeAsync().AsTask()); window.Close(); }
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static void Wait(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("UI persistence verification timed out");
            Drain(); Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(MainWindow window, string path)
    {
        Drain(); window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); png.Save(output);
    }
}
