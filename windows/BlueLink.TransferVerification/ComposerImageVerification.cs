using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Files;

internal static class ComposerImageVerification
{
    public static void Run(MainWindow window, System.Windows.Controls.RichTextBox input, FrameworkElement root, string directory, Action<bool, string> check)
    {
        // Independently encoded by Pillow/libwebp: transparent lossless, lossy,
        // animated, and extreme-aspect fixtures. No Windows codec is required.
        var samples = new Dictionary<string, string>
        {
            ["lossless"] = "UklGRiwAAABXRUJQVlA4TB8AAAAvL8AHEA8wgDH/8x84jLZt0/+fNm83HtH/CUgn/FQJAA==",
            ["lossy"] = "UklGRtQAAABXRUJQVlA4WAoAAAAQAAAALwAAHwAAQUxQSBgAAAABDzD/ERHCaNs2/f9p83bjEf2fgHTCT5VWUDgglgAAANAGAJ0BKjAAIAA+nUKaSSWjoiEwFVqosBOJagCxJYH+Afhn1lxc/iT+M0xr/gGEAfwD2/7t2/VUBXRFaokUAAD+6vv9571qQgLuLpXxOtgmIZLHVeDn3MCGYGzDDSmQ6Zpb+eytOa69LDi5BXDJlW0frXVyo/w70v+OINyIp+eJ6ETBGwSZ+kyggFgti7FIraXwCXwAAA==",
            ["animated"] = "UklGRpgAAABXRUJQVlA4WAoAAAASAAAALwAAHwAAQU5JTQYAAAAAAAAAAABBTk1GPAAAAAAAAAAAAC8AAB8AAGQAAAJWUDhMJAAAAC8vwAcQDzCAMf/zHzjUtm3D8P+ntafsEf0fCAAAAABQ9dFMCUFOTUYoAAAAAAAAAAAALwAAHwAAZAAAAFZQOEwPAAAALy/ABwAH0P+I/gciov8BAA==",
            ["wide"] = "UklGRiIAAABXRUJQVlA4TBUAAAAvz0cCAAcQ/Y/+BwAU6f9/ieh/KhwA"
        };
        void Layout()
        {
            root.Measure(new Size(1000, 720)); root.Arrange(new Rect(0, 0, 1000, 720)); root.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
        }
        var oldCache = FileInteractionService.ThumbnailDirectory;
        FileInteractionService.ThumbnailDirectory = Path.Combine(directory, "webp-cache", "Thumbnails");
        try
        {
            foreach (var sample in samples)
            {
                var path = Path.Combine(directory, sample.Key + ".webp"); File.WriteAllBytes(path, Convert.FromBase64String(sample.Value));
                var decoded = WebpBitmapDecoder.Load(path, 512);
                check(decoded.Bitmap.IsFrozen && Math.Max(decoded.Bitmap.PixelWidth, decoded.Bitmap.PixelHeight) <= 512, sample.Key + " WebP decoded at bounded preview size");
                check((decoded.Width, decoded.Height) == (sample.Key == "wide" ? (2000, 10) : (48, 32)), sample.Key + " original dimensions retained");
                var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), Path.GetFileName(path), "image/webp", new FileInfo(path).Length, path, "Completed");
                check(ChatThumbnailLoader.Load(attachment) is not null && FileInteractionService.LoadThumbnail(attachment) is not null, sample.Key + " message and file thumbnails render");
                check(FileInteractionService.ReadImageDimensions(attachment) == (decoded.Width, decoded.Height), "image details use WebP dimensions");
                input.Document = new FlowDocument(new Paragraph()); input.CaretPosition = input.Document.ContentEnd;
                window.InsertComposerAttachment(new(Guid.NewGuid(), path, Path.GetFileName(path), new FileInfo(path).Length)); Layout();
                var card = (Border)input.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<InlineUIContainer>().Single().Child;
                check(((Grid)card.Child).Children.OfType<Image>().Single().Source is DrawingImage, sample.Key + " composer renders thumbnail, not file icon");
                var previewResult = ImagePreviewWindow.TryCreate(path, "WebP fixture", Path.Combine(directory, "viewer"));
                check(previewResult.Window is not null && previewResult.Error is null,
                    sample.Key + " preview entry succeeds without an unhandled exception");
                var actualViewer = previewResult.Window!;
                check(((Image)actualViewer.FindName("PreviewImage")).Source is BitmapSource &&
                    new System.Windows.Interop.WindowInteropHelper(actualViewer).Handle == IntPtr.Zero,
                    sample.Key + " actual preview component stays offscreen");
                var viewerRoot = (FrameworkElement)actualViewer.Content;
                viewerRoot.Measure(new Size(900, 650)); viewerRoot.Arrange(new Rect(0, 0, 900, 650)); viewerRoot.UpdateLayout();
                var previewPixels = new RenderTargetBitmap(900, 650, 96, 96, PixelFormats.Pbgra32);
                previewPixels.Render(viewerRoot);
                check(previewPixels.PixelWidth == 900, sample.Key + " preview layout and rendering complete");
                actualViewer.Close();
                if (sample.Key == "lossless")
                {
                    var pixels = new byte[decoded.Bitmap.PixelWidth * decoded.Bitmap.PixelHeight * 4]; decoded.Bitmap.CopyPixels(pixels, decoded.Bitmap.PixelWidth * 4, 0);
                    check(pixels[3] == 0 && pixels[(16 * 48 + 24) * 4 + 3] == 255, "transparent WebP preserves clear and opaque pixels");
                    var viewer = new ImagePreviewWindow(path, "WebP fixture", Path.Combine(directory, "viewer"));
                    check(((Image)viewer.FindName("PreviewImage")).Source is BitmapSource && new System.Windows.Interop.WindowInteropHelper(viewer).Handle == IntPtr.Zero, "WebP opens in actual preview component without a desktop window");
                    viewer.Close();
                    var preview = ImagePreviewBuilder.CreateAsync(path, Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult();
                    try { check(preview.MimeType == "image/png" && new FileInfo(preview.Path).Length > 0, "transfer preview preserves WebP transparency"); }
                    finally { File.Delete(preview.Path); }
                }
            }
            var corrupt = Path.Combine(directory, "corrupt.webp"); File.WriteAllText(corrupt, "RIFFxxxxWEBPbroken");
            var truncated = Path.Combine(directory, "truncated.webp");
            File.WriteAllBytes(truncated, Convert.FromBase64String(samples["lossless"])[..24]);
            var missing = Path.Combine(directory, "missing.webp");
            foreach (var failurePath in new[] { corrupt, truncated, missing })
            {
                var failed = ImagePreviewWindow.TryCreate(failurePath, "Invalid image", Path.Combine(directory, "viewer"));
                check(failed.Window is null && !string.IsNullOrWhiteSpace(failed.Error),
                    Path.GetFileName(failurePath) + " returns a recoverable error without leaving a preview window");
            }
            using (var locked = new FileStream(Path.Combine(directory, "lossless.webp"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = ImagePreviewWindow.TryCreate(locked.Name, "Locked image", Path.Combine(directory, "viewer"));
                check(failed.Window is null && !string.IsNullOrWhiteSpace(failed.Error), "locked image is a recoverable preview error");
            }
            var recovered = ImagePreviewWindow.TryCreate(Path.Combine(directory, "lossless.webp"), "Retry", Path.Combine(directory, "viewer"));
            check(recovered.Window is not null && recovered.Error is null, "a valid preview still opens after decode and I/O failures");
            recovered.Window!.Close();
            input.Document = new FlowDocument(new Paragraph()); input.CaretPosition = input.Document.ContentEnd;
            window.InsertComposerAttachment(new(Guid.NewGuid(), corrupt, "corrupt.webp", new FileInfo(corrupt).Length)); Layout();
            var fallback = (Border)input.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<InlineUIContainer>().Single().Child;
            check(((Grid)fallback.Child).Children.OfType<StackPanel>().Any(), "corrupt WebP remains a readable removable file card");

            foreach (var height in new[] { 120d, 240d })
            foreach (var font in new[] { 13d, 18d })
            foreach (var scenario in new[] { "file", "image", "mixed" })
            {
                typeof(MainWindow).GetField("_composerPreferred", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, height);
                typeof(MainWindow).GetMethod("ApplyComposerHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
                input.FontSize = font; input.Document = new FlowDocument(new Paragraph()) { FontSize = font };
                input.CaretPosition = input.Document.ContentEnd;
                foreach (var name in scenario == "mixed" ? new[] { "report.pdf", "lossless.webp", "wide.webp" } : new[] { scenario == "file" ? "report.pdf" : "lossless.webp" })
                {
                    var path = Path.Combine(directory, name); input.CaretPosition = input.Document.ContentEnd;
                    window.InsertComposerAttachment(new(Guid.NewGuid(), path, name, new FileInfo(path).Length));
                }
                Layout();
                var pointer = input.CaretPosition.GetInsertionPosition(LogicalDirection.Forward)!;
                var nativeCaretMethod = input.Selection.GetType().GetMethod("CalculateCaretRectangle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                Rect NativeCaret() => (Rect)nativeCaretMethod.Invoke(null, new object[] { input.Selection, input.CaretPosition })!;
                var emptyCaret = NativeCaret();

                var paragraph = input.Document.Blocks.OfType<Paragraph>().Single();
                var cards = paragraph.Inlines.OfType<InlineUIContainer>().Select(i => (FrameworkElement)i.Child).ToArray();
                var centers = cards.Select(c => c.TranslatePoint(new Point(0, c.ActualHeight / 2), input).Y).ToArray();
                Console.WriteLine($"Attachment caret {font}/{scenario}: rect={emptyCaret}; centers={string.Join(',', centers)}");
                check(!emptyCaret.IsEmpty && centers.All(center => Math.Abs(center - (emptyCaret.Top + emptyCaret.Height / 2)) <= 1.2), "empty insertion caret vertically centers with " + scenario + "/" + font);
                check(emptyCaret.Height <= font * 1.7, "caret height follows text metrics rather than attachment height");
                input.CaretPosition = paragraph.Inlines.OfType<InlineUIContainer>().Last().ElementEnd; Layout();
                var boundaryCaret = NativeCaret();
                check(centers.All(center => Math.Abs(center - (boundaryCaret.Top + boundaryCaret.Height / 2)) <= 1.2), "navigation to object boundary keeps native caret centered");
                typeof(MainWindow).GetMethod("LoadComposer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null); Layout();
                check(window.ReadComposer().All(part => part.Text is null), "empty native anchors do not add draft text on reload");
                var reloadedCaret = NativeCaret();
                check(centers.All(center => Math.Abs(center - (reloadedCaret.Top + reloadedCaret.Height / 2)) <= 1.2), "draft reload retains centered text insertion");
                paragraph = input.Document.Blocks.OfType<Paragraph>().Single();
                var run = new Run("text"); paragraph.Inlines.Add(run); input.CaretPosition = run.ContentEnd; Layout();
                var textRect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                check(centers.All(center => Math.Abs(center - (textRect.Top + textRect.Height / 2)) <= 1.2), "typed text shares the attachment center");
                var image = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(directory, $"composer-webp-{scenario}-{font}-{height}.png")); encoder.Save(output);
            }
            input.FontSize = 13;
        }
        finally { FileInteractionService.ThumbnailDirectory = oldCache; }
    }
}
