using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyDrafts(string directory, string output)
    {
        var ledger = new DraftLedger();
        ledger.Load(new Dictionary<string, string> { ["PEER-A"] = "已有草稿\n  " });
        Check(ledger.Get("peer-a").Text == "已有草稿\n  ", "legacy draft exact text and case-insensitive identity");
        var first = ledger.Edit("b", "A"); ledger.Edit("b", "B"); ledger.Edit("b", "A");
        Check(!ledger.ClearAfterSend(first) && ledger.Get("b").Text == "A", "late send cannot erase A-B-A edit");
        ledger.Acknowledge(first);
        Check(ledger.Pending().Count == 1, "old disk acknowledgement preserves newer dirty draft");
        Check(ledger.ClearAfterSend(ledger.Get("b")) && ledger.Pending().Single().Text == "", "current send creates durable clear operation");
        ledger.Load(new Dictionary<string, string> { ["b"] = "A" });
        Check(!ledger.ClearAfterSend(first), "old callbacks cannot clear reloaded draft");
        ledger.Edit("a", "A"); ledger.Clear("a");
        Check(ledger.Get("b").Text == "A", "privacy clear remains peer-scoped");

        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        string peer = "", other = "";
        const string text = "未发送的草稿 👩‍💻\n第二行和尾部空格  ";
        var database = new BlueLinkDatabase(directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "message-history"));
            peer = main.ViewModel.Conversations.First(c => c.IsOffline).PeerId;
            other = main.ViewModel.Conversations.First(c => c.PeerId != peer).PeerId;
            WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));
            var root = DetachForRendering(main);
            Layout(root, 1000, 680);
            var input = (Wpf.Ui.Controls.RichTextBox)main.FindName("MessageInput");
            Check(input.IsEnabled && !main.ViewModel.IsConnected, "offline editor enabled with send disabled");
            input.Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text)));
            Check(main.ViewModel.DraftText == text, "real editor binding retains Unicode and whitespace");
            WaitForUiTask(main.ViewModel.FlushDraftsAsync());
            var load = database.LoadConversationsAsync(); WaitForUiTask(load);
            var before = load.Result.Single(c => c.PeerId == peer);
            Check(before.Draft == text, "draft stored in existing SQLite conversation field");
            WaitForUiTask(database.UpsertConversationMetadataAsync(before with { Draft = "stale cached value" }));
            var afterMetadata = database.LoadConversationsAsync(); WaitForUiTask(afterMetadata);
            Check(afterMetadata.Result.Single(c => c.PeerId == peer).Draft == text, "late metadata upsert cannot overwrite newer draft");
            WaitForUiTask(main.ViewModel.SelectConversationAsync(other));
            main.ViewModel.DraftText = "Other device draft";
            WaitForUiTask(main.ViewModel.FlushDraftsAsync());
            WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));
            Check(main.ViewModel.DraftText == text, "switching peers restores the right draft");
            var rejected = main.ViewModel.SendAsync(text); WaitForUiTask(rejected);
            Check(!rejected.Result && main.ViewModel.DraftText == text, "offline send rejected without clearing");
            var capture = main.ViewModel.CaptureDraft()!;
            main.ViewModel.DraftText = "newer edit";
            WaitForUiTask(main.ViewModel.CompleteDraftSendAsync(capture));
            Check(main.ViewModel.DraftText == "newer edit", "real model late completion protects newly typed text");
            WaitForUiTask(main.ViewModel.CompleteDraftSendAsync(main.ViewModel.CaptureDraft()!));
            var afterClear = database.LoadConversationsAsync(); WaitForUiTask(afterClear);
            Check(afterClear.Result.Single(c => c.PeerId == peer).Draft == "", "successful current completion clears persisted draft");
            main.ViewModel.DraftText = text;
            WaitForUiTask(main.ViewModel.FlushDraftsAsync());
            var after = database.LoadConversationsAsync(); WaitForUiTask(after);
            var saved = after.Result.Single(c => c.PeerId == peer);
            Check(saved.UnreadCount == before.UnreadCount && saved.LastActivityAt == before.LastActivityAt,
                "draft writes preserve unread and conversation timestamps");
            foreach (var theme in new[] { "light", "dark" })
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = language }));
                Capture(root, output, $"draft-offline-{theme}-{language}", 1000, 680);
                Check(input.IsEnabled && string.Concat(main.ReadComposer().Select(p => p.Text)) == text, "offline draft remains readable " + theme + language);
            }
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }

        var restored = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(restored.ViewModel.InitializeLocalStateAsync());
            WaitForUiTask(restored.ViewModel.SelectConversationAsync(peer));
            Check(restored.ViewModel.DraftText == text, "fresh model restores persisted draft after restart");
            WaitForUiTask(restored.ViewModel.ClearConversationAsync(peer));
            Check(restored.ViewModel.DraftText == "", "clear conversation removes its unsent draft");
            WaitForUiTask(restored.ViewModel.SelectConversationAsync(other));
            Check(restored.ViewModel.DraftText == "Other device draft", "clearing one conversation preserves another draft");
            for (var attempt = 0; attempt < 4; attempt++)
            {
                restored.ViewModel.DraftText = "pending clear " + attempt;
                var saving = restored.ViewModel.FlushDraftsAsync();
                var clearing = restored.ViewModel.ClearConversationAsync(other);
                WaitForUiTask(Task.WhenAll(saving, clearing));
                var cleared = database.LoadConversationsAsync(); WaitForUiTask(cleared);
                Check(cleared.Result.Single(c => c.PeerId == other).Draft == "", "concurrent save and clear converge without restoring old draft " + attempt);
            }
            WaitForUiTask(restored.ViewModel.ClearChatHistoryAsync());
            var empty = database.LoadConversationsAsync(); WaitForUiTask(empty);
            Check(empty.Result.All(c => c.Draft.Length == 0), "clear all messages also clears all drafts");

            // A missing conversation exercises the actual model's failed persistence path,
            // without changing permissions or damaging a database to inject an error.
            var modelLedger = (DraftLedger)typeof(MainViewModel).GetField("_drafts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restored.ViewModel)!;
            modelLedger.Edit("not-a-stored-peer", "preserve on failure");
            var failure = restored.ViewModel.FlushDraftsAsync(); WaitForUiTask(failure);
            Check(!failure.Result && modelLedger.Pending().Single().Text == "preserve on failure", "storage failure keeps pending text for retry");
            modelLedger.Load(Array.Empty<KeyValuePair<string, string>>());
        }
        finally { WaitForUiTask(restored.DisposeAsync().AsTask()); }
    }
}
