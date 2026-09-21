using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyProductSearch(string directory, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
        {
            AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Theme = theme, Language = language });
            var filename = string.Concat(Enumerable.Repeat("文件👩‍💻", 40)) + "Needle-needle" + new string('末', 60) + ".pdf";
            var body = new string('前', 500) + "Needle-needle" + new string('后', 500);
            var messages = new ObservableCollection<ChatItem> {
                new(Guid.NewGuid(), body, false, DateTimeOffset.Now, MessageStatus.Received),
                new(Guid.NewGuid(), "", true, DateTimeOffset.Now, MessageStatus.Sent, ChatItemKind.File,
                    [new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), filename, "application/pdf", 1024, State: "Canceled")]),
                new(Guid.NewGuid(), body, false, DateTimeOffset.Now, MessageStatus.Received, ChatItemKind.File,
                    [new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "caption-source.pdf", "application/pdf", 1024)])
            };
            var search = new MessageSearchWindow("QA", messages, false);
            try
            {
                var root = DetachForRendering(search);
                search.ApplyFilter("needle", HistoryKind.All);
                foreach (var width in new[] { 620, 900 })
                {
                    Capture(root, output, $"product-search-{theme}-{language}-{width}", width, 540);
                    var labels = Descendants<TextBlock>(root).Where(HighlightedText.GetPreview).ToArray();
                    Check(labels.Length == 3, "text, filename and attachment-caption search records realized");
                    foreach (var label in labels)
                    {
                        var rendered = string.Concat(label.Inlines.OfType<Run>().Select(run => run.Text));
                        Check(rendered.Contains("Needle", StringComparison.Ordinal), "late keyword remains visible at " + width);
                        Check(label.Inlines.OfType<Run>().Any(run => run.Text.Contains("Needle") &&
                            run.Foreground is SolidColorBrush color && color.Color == ((SolidColorBrush)root.FindResource("BlueBrush")).Color), "visible keyword has theme highlight");
                        if (HighlightedText.GetFileName(label)) Check(rendered.EndsWith(".pdf"), "extension preserved at " + width);
                        Check(label.ActualHeight <= label.FontSize * 3.5, "excerpt remains within two lines");
                    }
                    Check(messages[0].Text == body && messages[1].Attachments![0].FileName == filename, "copy/menu sources remain complete");
                }
            }
            finally { search.Close(); }
            var mainDirectory = Path.Combine(directory, theme + language);
            var main = new MainWindow(initializeRuntime: false, dataRoot: mainDirectory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main, mainDirectory, "message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = language }));
                main.ViewModel.AllTransfers.Clear();
                foreach (var state in new[] { TransferStatus.Completed, TransferStatus.Canceled, TransferStatus.Failed, TransferStatus.Rejected })
                    main.ViewModel.AllTransfers.Add(new TransferItem { Id = Guid.NewGuid(), Name = "QA-" + state + ".pdf", TotalBytes = 1024,
                        CompletedBytes = state == TransferStatus.Completed ? 1024 : 0, Status = state, Outgoing = true });
                main.OpenFileWorkspace(true);
                var root = DetachForRendering(main);
                Capture(root, output, $"product-files-{theme}-{language}", 1000, 640);
                Check(BlueLink.Localization.Strings.Language == language, "file view uses requested language");
                Check(((SolidColorBrush)root.FindResource("SurfaceBrush")).Color.R < 100 == (theme == "dark"), "file view uses requested theme");
                var filter=main.CreateFileColumnEditor("Status");
                Check(filter.Choices.Keys.ToHashSet().SetEquals(new[] {"Active","Completed","Incomplete","Failed","Rejected","Canceled"}),"all terminal choices available; reset means all");
                SetFileColumn(main,"Status","Canceled");
                Layout(root, 1000, 640);
                var list = (ListBox)main.FindName("TransferList");
                Check(list.Items.Cast<TransferItem>().All(i => i.Status == TransferStatus.Canceled) && list.Items.Count == 1, "cancel filter excludes failed/rejected");
            }
            finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
    }
}
