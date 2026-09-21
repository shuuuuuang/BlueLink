using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink;
using BlueLink.Domain;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifySearchClickSelection(string directory, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            var fixture = Path.Combine(directory, "click-selection-" + theme);
            var main = new MainWindow(false, fixture);
            MessageSearchWindow? search = null;
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main, fixture, "message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
                var now = DateTimeOffset.Now;
                var outgoing = new ChatItem(Guid.NewGuid(), "BlueLink QA needle Windows to Android", true, now, MessageStatus.Delivered);
                var incoming = new ChatItem(Guid.NewGuid(), "对端 needle 文本可以选择复制", false, now.AddMinutes(-1), MessageStatus.Received);
                var picture = Path.GetFullPath("design/brand/final/bluelink-final-logo.png");
                var filePath = Path.Combine(fixture, "needle.txt"); File.WriteAllText(filePath, "keep bytes");
                var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "needle.png", "image/png", new FileInfo(picture).Length, picture, "Completed");
                var photo = new ChatItem(Guid.NewGuid(), "", false, now.AddMinutes(-2), MessageStatus.Received, ChatItemKind.File, [attachment]);
                var fileAttachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "needle.txt", "text/plain", 10, filePath, "Completed");
                var file = new ChatItem(Guid.NewGuid(), "", true, now.AddMinutes(-3), MessageStatus.Delivered, ChatItemKind.File, [fileAttachment]);
                var source = new ObservableCollection<ChatItem>([outgoing, incoming, photo, file]);
                search = new MessageSearchWindow("QA 手机", source, true, main.ViewModel);
                search.ApplyFilter("needle", HistoryKind.All);
                var root = new System.Windows.Documents.AdornerDecorator { Child = DetachForRendering(search) }; Layout(root,860,740);
                var results = (ListBox)search.FindName("Results");
                var text = Descendants<RichTextBox>(root).Single(t => t.Name == "SearchSelectableText" && HighlightedText.GetText(t) == outgoing.Text);
                var peer = new RichTextBoxAutomationPeer(text);
                var provider = (ITextProvider)peer.GetPattern(PatternInterface.Text);
                Check(text.IsReadOnly && text.IsHitTestVisible && provider is not null, "search uses a selectable read-only native text control");
                provider!.DocumentRange.FindText("BlueLink", false, false).Select(); DrainDispatcher();
                Check(text.Selection.Text == "BlueLink" && search.ResultTextToCopy(outgoing) == "BlueLink", "UIA substring selection and context Copy preserve only selected text");
                Check(search.ResultTextToCopy(incoming) == incoming.Text, "a selection never changes another message's Copy payload");
                var runs = text.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToArray();
                var hit = runs.Single(run => run.Text == "needle");
                Check(hit.Background is SolidColorBrush hitBrush && text.SelectionBrush is SolidColorBrush selectedBrush && hitBrush.Color != selectedBrush.Color && text.SelectionOpacity == 0,
                    "manual selection is opaque cyan, distinct from gold search matches");
                Check(text.SelectionTextBrush is SolidColorBrush ink && ink.Color == Color.FromRgb(16,33,60), "manual selection has dark readable text");
                var textCard = Descendants<Border>(results).Single(card => card.Name == "ResultCard" && Equals(card.DataContext, text.DataContext));
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                textCard.RaiseEvent(mouse);
                Check(!mouse.Handled && typeof(MessageSearchWindow).GetField("_resultMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search) is null,
                    "actual text left-click neither opens a menu nor consumes text selection");
                var openings = new List<ChatAttachment>();
                foreach (var name in new[] { "needle.png", "needle.txt" })
                {
                    var label = Descendants<TextBlock>(results).Single(t => t.Name == "SearchMessageText" && HighlightedText.GetText(t) == name);
                    var card = Descendants<Border>(results).Single(b => b.Name == "ResultCard" && Equals(b.DataContext, label.DataContext));
                    Check(search.ActivateResult(card, (value, gallery) => { openings.Add(value); Check(gallery.Any(a => a.AttachmentId == attachment.AttachmentId), "file activation keeps the filtered preview gallery"); }), "file left action activates the matching attachment");
                }
                Check(openings.Count == 2 && openings[0].LocalPath == picture && openings[1].LocalPath == filePath, "left activation resolves image and ordinary file correctly without navigating to the original message");
                Check(!search.ActivateResult(textCard, (_, _) => throw new Exception("text must not open files")), "text activation never dispatches a file action");
                Check(search.SelectedMessage is null, "left actions keep search open");
                Capture(root, output, "search-selection-" + theme, 860,740);
                var other = Descendants<RichTextBox>(root).Single(t => t.Name == "SearchSelectableText" && HighlightedText.GetText(t) == incoming.Text);
                ((ITextProvider)new RichTextBoxAutomationPeer(other).GetPattern(PatternInterface.Text)).DocumentRange.FindText("对端", false, false).Select(); DrainDispatcher();
                Check(text.Selection.IsEmpty && other.Selection.Text == "对端", "starting another text selection clears the old one");
                search.SetSearchSelectionMode(true, outgoing.Id); Layout(root,860,740);
                Check(other.Selection.IsEmpty && !text.IsHitTestVisible && !text.Focusable, "batch selection clears text selection and delegates pointer input to row gestures");
                Check(!search.ActivateResult(textCard, (_, _) => throw new Exception("batch must not open files")), "batch mode suppresses ordinary result activation");
                search.SetSearchSelectionMode(false); Layout(root,860,740);
                Check(text.IsHitTestVisible && text.Focusable, "leaving batch restores native text selection");
                search.ApplyFilter("", HistoryKind.All); Layout(root,860,740);
                Check(Descendants<RichTextBox>(root).Where(t => t.Name == "SearchSelectableText").SelectMany(t => t.Document.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).All(run => run.Background is null || run.Background is SolidColorBrush { Color.A: 0 }), "clearing search removes the gold match background");
                main.ViewModel.Messages.Clear(); main.ViewModel.Messages.Add(outgoing);
                var chatRoot = new System.Windows.Documents.AdornerDecorator { Child = DetachForRendering(main) }; Layout(chatRoot,1000,740);
                var chat = Descendants<TextBox>(chatRoot).Single(t => t.Name == "MessageText" && t.Text == outgoing.Text);
                ((ITextProvider)new TextBoxAutomationPeer(chat).GetPattern(PatternInterface.Text)).DocumentRange.FindText("BlueLink", false, false).Select(); DrainDispatcher();
                Check(chat.SelectedText == "BlueLink" && chat.SelectionOpacity == 1 && ((SolidColorBrush)chat.SelectionBrush).Color == ((SolidColorBrush)text.SelectionBrush).Color,
                    "chat UIA text selection shares the same contrasting color as search");
                Capture(chatRoot, output, "chat-selection-" + theme, 1000,740);
                Check(new System.Windows.Interop.WindowInteropHelper(search).Handle == IntPtr.Zero && new System.Windows.Interop.WindowInteropHelper(main).Handle == IntPtr.Zero, "selection acceptance creates no foreground HWND");
            }
            finally { search?.Close(); WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
    }
}
