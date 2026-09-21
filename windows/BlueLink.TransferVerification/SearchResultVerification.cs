using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BlueLink;
using BlueLink.Domain;

internal static class SearchResultVerification
{
    internal static void Run(string directory)
    {
        Exception? failure = null;
        var checks = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                var app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Directory.CreateDirectory(directory);
                void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
                SearchExcerptVerification.Run(Check, directory);
                var first = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "first.pdf", "application/pdf", 1024);
                var matched = first with { AttachmentId = Guid.NewGuid(), TransferId = Guid.NewGuid(), FileName = "Needle-报告.pdf" };
                var file = new ChatItem(Guid.NewGuid(), "caption", false, DateTimeOffset.Now, MessageStatus.Received,
                    ChatItemKind.File, [first, matched]);
                var text = new ChatItem(Guid.NewGuid(), "needle full text\n末尾空格  ", false, DateTimeOffset.Now, MessageStatus.Received);
                Check(SearchResultActions.Attachment(file, " NEEDLE ") == matched, "trimmed case-insensitive query chooses matching attachment");
                Check(SearchResultActions.Attachment(file, "caption") == first, "body match falls back to first attachment");
                Check(SearchResultActions.Attachment(text, "needle") is null, "text results have no fabricated attachment");
                var main = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(directory, "data"));
                var messages = new ObservableCollection<ChatItem> { text, file };
                var search = new MessageSearchWindow("QA search", messages, false, main.ViewModel);
                search.ApplyFilter("needle", HistoryKind.All);
                var results = (ListBox)search.FindName("Results");
                Check(results.Items.Count == 2, "text and filename remain searchable");
                results.SelectedItem = results.Items[0];
                Check(search.SelectedMessage is null, "selection no longer navigates or closes search");
                var menu = search.BuildResultMenu(text);
                Check(Tags(menu).SequenceEqual(new[] { "message", "copy-text", "select", "delete" }), "text menu orders locate, copy, selection and delete without message details");
                foreach (var state in Enum.GetValues<TransferStatus>())
                {
                    var path = Path.Combine(directory, "generated.txt"); File.WriteAllText(path, "BlueLink QA");
                    var attachment = matched with { State = state.ToString(), LocalPath = path };
                    var item = file with { Attachments = [attachment] };
                    messages[1] = item;
                    menu = search.BuildResultMenu(item);
                    var tags = Tags(menu);
                    Check(tags.Count(t => t == "copy-name") == 1, "filename copy exactly once for " + state);
                    Check(tags.Contains("open") == (state == TransferStatus.Completed), "open gated on completion for " + state);
                    Check(menu.Items.OfType<MenuItem>().Where(i => i.Tag as string is "pause" or "resume" or "cancel" or "retry").All(i => !i.IsEnabled), "offline transport controls disabled for " + state);
                }
                var missing = file with { Attachments = [matched with { State = "Completed", LocalPath = Path.Combine(directory, "missing-QA.pdf") }] };
                messages[1] = missing;
                menu = search.BuildResultMenu(missing);
                Check(Tags(menu).Contains("copy-name") && !Tags(menu).Contains("open") && !Tags(menu).Contains("copy"), "missing file name remains copyable without file operations");
                var live = new TransferItem { Id = matched.TransferId, Name = matched.FileName, TotalBytes = 1024, Outgoing = false, Status = TransferStatus.Transferring };
                main.ViewModel.AllTransfers.Add(live);
                Check(SearchResultActions.Current(matched with { State = "Completed" }, main.ViewModel.AllTransfers).State == "Transferring", "live state overrides stale completed message");
                var resolved = SearchResultActions.Current(matched, main.ViewModel.AllTransfers);
                Check(resolved.AttachmentId == matched.AttachmentId && resolved.FileName == matched.FileName, "resolution retains original attachment identity and filename");
                Check(((Wpf.Ui.Controls.TextBox)search.FindName("QueryInput")).Text == "needle", "menus and live record updates retain query");
                messages.Remove(text);
                var refreshed = search.RefreshResultsAsync(false);
                if (!refreshed.IsCompleted)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    _ = refreshed.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                refreshed.GetAwaiter().GetResult();
                Check(results.Items.Count == 1 && search.SelectedMessage is null, "deleted records leave search without navigation");
                foreach (var source in new object[] { matched, live })
                {
                    var shared = new ContextMenu(); shared.Items.Add(new MenuItem { Tag = "copy-name" });
                    MainWindow.ConfigureFileContextMenu(shared, source, false);
                    Check(((MenuItem)shared.Items[0]).Visibility == Visibility.Visible, "shared name action accepts " + source.GetType().Name);
                }
                search.Close(); main.Close(); app.Shutdown();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Search result regression failed", failure);
        File.WriteAllText(Path.Combine(directory, "search-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Search result verification passed: {checks.Count} checks");
    }
    private static string[] Tags(ContextMenu menu) => menu.Items.OfType<MenuItem>()
        .Where(i => i.Visibility == Visibility.Visible).Select(i => (string)i.Tag).ToArray();
}
