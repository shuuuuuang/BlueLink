using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyBubbleShapes(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "bubble-shapes");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "message-history"));
            var image = main.ViewModel.Messages.SelectMany(m => m.Attachments ?? []).First(a => a.IsImage && a.CanOpen);
            foreach (var theme in new[] { "light", "dark" })
            foreach (var outgoing in new[] { false, true })
            {
                AppearanceService.Apply(main.ViewModel.Settings with { Theme = theme });
                var list = new ListBox {
                    Style = (Style)main.FindResource("DefaultListBoxStyle"),
                    ItemContainerStyle = (Style)main.FindResource("TransparentListItemStyle"),
                    ItemTemplate = (DataTemplate)main.FindResource("MessageTemplate") };
                // Reuse the same list while varying thumbnail availability and sender direction.
                foreach (var kind in new[] { "text", "image", "image-active", "hidden-image", "missing-image", "file", "image" })
                {
                    var visibleImage = kind is "image" or "image-active";
                    list.DataContext = new { Settings = main.ViewModel.Settings with { ShowImageThumbnails = kind != "hidden-image" } };
                    var attachment = image with { State = kind == "image-active" ? "Transferring" : "Completed",
                        LocalPath = kind == "missing-image" ? Path.Combine(directory, "missing.png") : image.LocalPath,
                        PreviewPath = kind == "image-active" ? image.LocalPath : null, MimeType = kind == "file" ? "application/pdf" : "image/png" };
                    list.ItemsSource = new[] { new ChatItem(Guid.NewGuid(), kind == "text" ? "QA bubble shape" : "", outgoing,
                        DateTimeOffset.Now, MessageStatus.Delivered, kind == "text" ? ChatItemKind.Text : ChatItemKind.File,
                        kind == "text" ? [] : [attachment]) };
                    var scene = $"bubble-{theme}-{(outgoing ? "outgoing" : "incoming")}-{kind}";
                    Capture(list, output, scene, 560, 260);
                    var bubble = Descendants<Border>(list).Single(b => b.Name == "Bubble");
                    var directional = outgoing ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4);
                    Check(bubble.CornerRadius == directional, "text container keeps its sender corner: " + scene);
                    if (kind == "text") continue;
                    var border = Descendants<Border>(list).Single(b => b.Name == "AttachmentBorder");
                    var thumbnail = Descendants<Image>(list).Single(i => i.Name == "Thumbnail");
                    Check((thumbnail.Visibility == Visibility.Visible) == visibleImage, "actual thumbnail visibility: " + scene);
                    Check(border.CornerRadius == (visibleImage ? new CornerRadius(14) : directional),
                        "visible thumbnail has equal corners; file fallback stays directional: " + scene);
                    if (visibleImage)
                        Check(thumbnail.Clip is RectangleGeometry { RadiusX: 14, RadiusY: 14 } && border.BorderThickness == new Thickness(0),
                            "image clip and background have identical corner radii: " + scene);
                }
            }
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }
}
