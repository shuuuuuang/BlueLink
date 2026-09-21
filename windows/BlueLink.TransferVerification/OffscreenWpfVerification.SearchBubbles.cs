using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueLink;
using BlueLink.Domain;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifySearchBubbles(string directory, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            var fixture = Path.Combine(directory, "search-bubbles-" + theme);
            var main = new MainWindow(false, fixture);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main, fixture, "message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
                var picture = Path.GetFullPath("design/brand/final/bluelink-final-logo.png");
                var now = DateTimeOffset.Now;
                var file = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "项目设计方案-图片预览.png", "image/png", new FileInfo(picture).Length, picture, "Completed");
                var incoming = new ChatItem(Guid.NewGuid(), "对端消息：请确认设计方案", false, now.AddMinutes(-1), MessageStatus.Received);
                var outgoing = new ChatItem(Guid.NewGuid(), "本机消息：已收到，稍后回复", true, now.AddMinutes(-2), MessageStatus.Delivered);
                var image = new ChatItem(Guid.NewGuid(), "", true, now, MessageStatus.Delivered, ChatItemKind.File, [file]);
                var search = new MessageSearchWindow("QA 手机", new ObservableCollection<ChatItem>([image, incoming, outgoing]), true);
                try
                {
                    search.ApplyFilter("", HistoryKind.All);
                    var root = DetachForRendering(search); Layout(root,860,740);
                    var results=(ListBox)search.FindName("Results");
                    var thumbnail=Descendants<Border>(results).Single(border=>border.Name=="SearchThumbnail" && border.Visibility==Visibility.Visible);
                    Check(thumbnail.Background is ImageBrush { ImageSource: not null } && thumbnail.ActualWidth<=240 && thumbnail.ActualHeight<=144,"search uses bounded chat thumbnail geometry");
                    Check(thumbnail.CornerRadius==new CornerRadius(10),"search thumbnails have four equal corners");
                    var caption=Descendants<TextBlock>(results).Single(text=>text.Name=="SearchMessageText" && Equals(text.DataContext,thumbnail.DataContext));
                    Check(HighlightedText.GetText(caption)==file.FileName && caption.ActualHeight>0,"image search result displays the actual filename below thumbnail");
                    var sizeLabel=Descendants<TextBlock>(results).Single(text=>text.Name=="SearchFileDetail" && Equals(text.DataContext,thumbnail.DataContext));
                    Check(sizeLabel.Text==file.SizeText && !sizeLabel.Text.Contains(file.StateText),"file metadata shows size once without redundant completed status");
                    Check(Math.Abs(sizeLabel.TranslatePoint(new Point(0,sizeLabel.ActualHeight/2),root).Y-caption.TranslatePoint(new Point(0,caption.ActualHeight/2),root).Y)<2,
                        "image filename and size share one visual line");
                    Check(sizeLabel.TranslatePoint(new Point(),root).X>=caption.TranslatePoint(new Point(caption.ActualWidth,0),root).X,
                        "file size reserves its own space after the filename");
                    var photoBubble=Descendants<Border>(results).Single(border=>border.Name=="ResultCard" && Equals(border.DataContext,thumbnail.DataContext));
                    Check(photoBubble.BorderThickness==new Thickness(0),"image preview bubble has no file outline");
                    var columns=Descendants<StackPanel>(results).Where(panel=>panel.Name=="SearchMessageColumn").ToArray();
                    Check(columns.Count(panel=>panel.HorizontalAlignment==HorizontalAlignment.Right)==2 && columns.Count(panel=>panel.HorizontalAlignment==HorizontalAlignment.Left)==1,"text and image results preserve sender alignment");
                    var footers=Descendants<TextBlock>(results).Where(text=>text.Name=="SearchStatusTime").ToArray();
                    Check(footers.Length==3 && footers.Any(text=>text.Text==image.StatusText+" · "+image.CreatedAt.LocalDateTime.ToString("HH:mm")),"search time follows status on the same line");
                    Check(!Descendants<TextBlock>(results).Any(text=>text.Text.Contains("QA 手机") || text.Text.StartsWith("本机 ·")),"search rows omit device names and local-device labels");
                    Capture(root,output,$"search-bubbles-{theme}",860,740);
                    Layout(root,620,540);
                    Check(columns.All(panel=>panel.ActualWidth<=440),"bubble layout remains bounded at minimum window width");
                    Capture(root,output,$"search-bubbles-small-{theme}",620,540);
                    search.ApplyFilter("消息", HistoryKind.All); Layout(root,860,740);
                    var outgoingText = Descendants<RichTextBox>(results).Single(text => text.Name == "SearchSelectableText" && HighlightedText.GetText(text) == outgoing.Text);
                    var match = outgoingText.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Run>()).Single(run => run.Text == "消息");
                    Check(match.Background is SolidColorBrush { Color.A: 255 } && match.Foreground is SolidColorBrush ink && ink.Color != ((SolidColorBrush)outgoingText.Foreground).Color,
                        "outgoing search match has an opaque contrasting background and distinct ink in " + theme);
                    Check(outgoingText.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Run>()).Where(run => run.Text != "消息").All(run => run.Background is null || run.Background is SolidColorBrush { Color.A: 0 }),
                        "only the matched text receives a highlight background");
                    Capture(root,output,$"search-highlight-{theme}",860,740);
                    search.ApplyFilter("", HistoryKind.All); Layout(root,860,740);
                    Check(Descendants<TextBlock>(results).Where(text => text.Name == "SearchMessageText").SelectMany(text => text.Inlines.OfType<System.Windows.Documents.Run>()).All(run => run.Background is null || run.Background is SolidColorBrush { Color.A: 0 }),
                        "clearing the query removes search highlight backgrounds");
                    foreach(var state in new[] { "Transferring", "Failed", "Canceled", "Paused" })
                    {
                        var current = file with { State=state };
                        var probe = new MessageSearchWindow("QA",new ObservableCollection<ChatItem>([image with { Attachments=[current] }]));
                        try
                        {
                            probe.ApplyFilter("",HistoryKind.All);
                            var probeRoot=DetachForRendering(probe); Layout(probeRoot,620,540);
                            Check(Descendants<TextBlock>(probeRoot).Single(t=>t.Name=="SearchStatusTime").Text.StartsWith(current.StateText+" · "),
                                "search retains actionable attachment state in its single footer: "+state);
                        }
                        finally { probe.Close(); }
                    }
                    Check(new System.Windows.Interop.WindowInteropHelper(search).Handle==IntPtr.Zero,"bubble preview verification creates no foreground window");
                }
                finally { search.Close(); }
            }
            finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
    }
}
