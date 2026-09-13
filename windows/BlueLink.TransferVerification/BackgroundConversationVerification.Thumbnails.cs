using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Files;

internal static partial class BackgroundConversationVerification
{
    private static void VerifyChatThumbnailGeometry(MainWindow window, FrameworkElement root, string output, Action<bool, string> check)
    {
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "chat-thumbnail-cases.tsv")).Where(x => !x.StartsWith('#') && x.Length > 0))
        {
            var columns = line.Split('\t'); var n = columns.Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var g = ChatThumbnailGeometry.Calculate(n[0], n[1], n[2], n[3]);
            var actual = new[] { g.Scale, g.Width, g.Height, g.CropX, g.CropY, g.CropWidth, g.CropHeight };
            check(actual.Zip(n.Skip(4)).All(p => Math.Abs(p.First - p.Second) < 0.00001), "shared thumbnail geometry: " + columns[0]);
        }
        foreach (var theme in new[] { "light", "dark" })
        foreach (var (width, height) in new[] { (24, 32), (32, 1200), (1200, 32), (120, 2400), (2400, 120), (800, 600), (100, 80), (1000, 1000) })
        {
            Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, ShowImageThumbnails = true }));
            var source = new DrawingVisual();
            using (var dc = source.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(Brushes.DeepSkyBlue, null, new Rect(width * .25, height * .25, width * .5, height * .5));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(source);
            var path = Path.Combine(output, $"geometry-source-{width}x{height}.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), Path.GetFileName(path), "image/png", new FileInfo(path).Length,
                LocalPath: path, State: "Completed");
            var image = ChatThumbnailLoader.Load(attachment)!;
            var cached = ChatThumbnailLoader.Load(attachment)!;
            var g = ChatThumbnailGeometry.Calculate(width, height);
            check(Math.Abs(image.Width - g.Width) < .51 && Math.Abs(image.Height - g.Height) < .51 &&
                image.Width == cached.Width && image.Height == cached.Height, $"thumbnail dimensions survive cache: {theme}/{width}x{height}");
            var pixel = new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
            check(pixel[0] > 240 && pixel[1] > 150 && pixel[2] < 10 && pixel[3] == 255, "thumbnail uses the actual source center: " + width + "x" + height);
            if (width < 48 || height < 48)
            {
                image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                check(pixel[3] == 0, "sub-minimum image is padded without enlargement: " + width + "x" + height);
            }
            window.ViewModel.Messages.Clear();
            window.ViewModel.Messages.Add(new(Guid.NewGuid(), "", theme == "dark", DateTimeOffset.Now, MessageStatus.Received, ChatItemKind.Image, [attachment]));
            Drain(); root.UpdateLayout(); Drain();
            var element = Descendants<Image>(root).Single(x => x.Name == "Thumbnail");
            check(element.IsVisible && Math.Abs(element.ActualWidth - g.Width) < .51 && Math.Abs(element.ActualHeight - g.Height) < .51,
                $"real chat image keeps its computed geometry: {theme}/{width}x{height}");
            Capture(root, Path.Combine(output, $"geometry-{theme}-{width}x{height}.png"));
        }
        // The 48px minimum still holds while the progress overlay is present.
        var pending = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "pending.png", "image/png", 1000,
            State: "Transferring", PreviewPath: Path.Combine(output, "geometry-source-24x32.png"), CompletedBytes: 400);
        window.ViewModel.Messages.Clear();
        window.ViewModel.Messages.Add(new(Guid.NewGuid(), "", false, DateTimeOffset.Now, MessageStatus.Received, ChatItemKind.Image, [pending]));
        Drain(); root.UpdateLayout(); Drain();
        var thumbnail = Descendants<Image>(root).Single(x => x.Name == "Thumbnail");
        var overlay = Descendants<Border>(root).Single(x => x.Name == "ImageProgress");
        check(thumbnail.ActualWidth == 48 && thumbnail.ActualHeight == 48 && overlay.IsVisible && overlay.ActualWidth <= 48,
            "progress overlay does not enlarge the minimum thumbnail");
        check(ChatThumbnailLoader.Load(pending with { PreviewPath = null, LocalPath = pending.PreviewPath }) is null,
            "partial original is never decoded as a chat thumbnail");
        Capture(root, Path.Combine(output, "geometry-progress-small.png"));
    }
}
