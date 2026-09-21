using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Files;
using System.Windows.Controls;
using System.Windows.Input;

internal static class ComposerVerification
{
    public static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Verify(directory); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); if (!thread.Join(TimeSpan.FromMinutes(2))) throw new TimeoutException();
        if (failure is not null) throw failure;
    }
    private static void Verify(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = 0; void Check(bool pass, string reason) { if (!pass) throw new Exception(reason); checks++; }
        foreach (var dimensions in new[] { (2000d, 10d), (10d, 2000d), (200d, 200d), (1d, 1d), (1200d, 800d) })
        {
            var g = ChatThumbnailGeometry.Calculate(dimensions.Item1, dimensions.Item2, 96, 64);
            Check(g.Width is >= 48 and <= 96 && g.Height is >= 48 and <= 64, "bounded compact viewport");
            Check(g.Scale <= 1 && Math.Abs(g.CropX * 2 + g.CropWidth - dimensions.Item1) < .01 &&
                Math.Abs(g.CropY * 2 + g.CropHeight - dimensions.Item2) < .01, "no upscaling and centered extreme crop");
        }
        var square = ChatThumbnailGeometry.Calculate(200, 200, 96, 64);
        Check(square.Width == 64 && square.Height == 64, "square image remains square");
        var store = new ComposerDrafts(Path.Combine(directory, "store"));
        store.SetHeight(280); Check(new ComposerDrafts(Path.Combine(directory, "store")).Height == 280, "height survives reopening");
        Check(ComposerDrafts.ClampHeight(-5, 800) == 120 && ComposerDrafts.ClampHeight(900, 800) == 360, "height hard bounds");
        Check(ComposerDrafts.ClampHeight(360, 380) == 200, "small windows keep conversation space");
        Check(ComposerDrafts.ClampHeight(double.NaN, 800) == 120, "invalid saved heights are bounded");
        foreach (var count in new[] { 0, 1, 10, 11, 100 }) Check(ComposerDrafts.AcceptsDrop(count) == (count is >= 1 and <= 10), "drop limit " + count);
        var source = Path.Combine(directory, "report.pdf"); File.WriteAllText(source, "QA PDF bytes");
        var file = new ComposerAttachment(Guid.NewGuid(), source, "report.pdf", new FileInfo(source).Length);
        var a = new ComposerPart(Guid.NewGuid(), "text1"); var f = new ComposerPart(file.Id, File: file); var b = new ComposerPart(Guid.NewGuid(), "text2");
        store.Edit("peer", [a, f, b]); var taken = store.Take("peer");
        Check(taken.Select(p => p.Text ?? p.File!.Name).SequenceEqual(new[] { "text1", "report.pdf", "text2" }), "ordered text/file/text messages");
        store.Edit("peer", [new(Guid.NewGuid(), "new text")]); store.Acknowledge("peer", a.Id);
        var restored = new ComposerDrafts(Path.Combine(directory, "store"));
        Check(restored.Get("peer").Select(p => p.Text ?? p.File!.Name).SequenceEqual(new[] { "report.pdf", "text2", "new text" }), "restart restores only unacknowledged work ahead of new typing");
        Check(ComposerDrafts.Messages(restored.Get("peer")).Last().Text == "text2new text", "adjacent text segments merge");
        Check(restored.Get("other").Count == 0 && File.Exists(source), "peer isolation and original file preservation");
        var app = App.CreateResourceOnlyHost(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(false, Path.Combine(directory, "ui"));
        void Wait(Task task) { var frame = new DispatcherFrame(); task.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false))); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
        try
        {
            Wait(DesktopAcceptance.InitializeAsync(window, Path.Combine(directory, "ui"), "message-history"));
            var peer = window.ViewModel.Conversations.First(c => c.IsOffline).PeerId; Wait(window.ViewModel.SelectConversationAsync(peer));
            var input = (Wpf.Ui.Controls.RichTextBox)window.FindName("MessageInput");
            var layoutRoot = (FrameworkElement)window.Content;
            var placeholder = (TextBlock)window.FindName("ComposerPlaceholder");
            foreach (var fontSize in new[] { 13d, 18d })
            {
                input.FontSize = fontSize; input.Document.FontSize = fontSize;
                layoutRoot.Measure(new Size(1000, 720)); layoutRoot.Arrange(new Rect(0, 0, 1000, 720)); layoutRoot.UpdateLayout();
                var insertion = input.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                layoutRoot.UpdateLayout();
                var origin = placeholder.TranslatePoint(new Point(), input);
                Console.WriteLine($"Composer alignment {fontSize}: caret={insertion}, placeholder={origin}, font={input.FontSize}/{placeholder.FontSize}");
                var pixel = 1 / VisualTreeHelper.GetDpi(input).DpiScaleX;
                Check(!insertion.IsEmpty && Math.Abs(insertion.X - origin.X) <= pixel && Math.Abs(insertion.Y - origin.Y) <= pixel,
                    "empty placeholder shares the actual WPF insertion origin");
                Check(placeholder.FontSize == input.FontSize && placeholder.FontFamily.Equals(input.FontFamily),
                    "placeholder shares editor typography after size changes");
                var capture = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32); capture.Render(layoutRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                using var output = File.Create(Path.Combine(directory, $"composer-empty-{fontSize}.png")); encoder.Save(output);
            }
            input.FontSize = 13; input.Document.FontSize = 13;
            var externalDrop = FileDragDropService.CreateCopyDataObject(source);
            Check(!FileDragDropService.IsOutboundDrag(externalDrop), "clipboard copy and external file drops remain eligible");
            externalDrop.SetData(FileDragDropService.OutboundDragFormat, "BlueLink");
            Check(FileDragDropService.IsOutboundDrag(externalDrop) && FileDragDropService.ExtractFilePaths(externalDrop).Single() == source,
                "outbound marker preserves standard file data for other applications");
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            foreach (var count in new[] { 1, 11 })
            {
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Language = language }));
                var accepts = window.ShowFileDropFeedback(count, composer: true);
                layoutRoot.Measure(new Size(1000, 720)); layoutRoot.Arrange(new Rect(0, 0, 1000, 720)); layoutRoot.UpdateLayout();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                layoutRoot.UpdateLayout();
                var caption = (TextBlock)window.FindName("FileDropTitle");
                Console.WriteLine($"Drop {language}/{count}: {caption.Text}, wrapping={caption.TextWrapping}, actual={caption.RenderSize}, desired={caption.DesiredSize}");
                var overlay = (Grid)window.FindName("FileDropOverlay");
                Check(accepts == (count == 1), "offline composer accepts up to ten staged files");
                foreach (var name in new[] { "FileDropIcon", "FileDropTitle", "FileDropDetail" })
                {
                    var element = (FrameworkElement)window.FindName(name);
                    var bounds = element.TransformToAncestor(overlay).TransformBounds(new Rect(element.RenderSize));
                    Check(bounds.Top >= 0 && bounds.Bottom <= overlay.ActualHeight + .1 && bounds.Left >= 0 && bounds.Right <= overlay.ActualWidth + .1,
                        $"compact drop content fits minimum composer: {name}/{language}/{count}");
                }
                var capture = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32); capture.Render(layoutRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                using var output = File.Create(Path.Combine(directory, $"composer-drop-{language}-{count}.png")); encoder.Save(output);
            }
            ((Grid)window.FindName("FileDropOverlay")).Visibility = Visibility.Collapsed;
            Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Language = "zh-CN" }));

            input.Document = new FlowDocument(new Paragraph(new Run("replace me")));
            input.SelectAll(); window.InsertComposerText("粘贴文字 🌍");
            Check(input.Selection.IsEmpty && string.Concat(window.ReadComposer().Select(p => p.Text)) == "粘贴文字 🌍", "plain paste replaces selection and collapses caret at end");
            window.InsertComposerText(" continued");
            Check(string.Concat(window.ReadComposer().Select(p => p.Text)) == "粘贴文字 🌍 continued", "typing after paste appends instead of replacing pasted text");
            var clipAttachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), file.Name, "application/pdf", file.Size, Path.GetFullPath(file.Path), "Completed");
            var clipTime = DateTimeOffset.UtcNow;
            var clipMessages = new[] {
                new ChatItem(Guid.NewGuid(), "before", false, clipTime, MessageStatus.Received),
                new ChatItem(Guid.NewGuid(), "", false, clipTime.AddSeconds(1), MessageStatus.Received, ChatItemKind.File, [clipAttachment]),
                new ChatItem(Guid.NewGuid(), "after 🌍", false, clipTime.AddSeconds(2), MessageStatus.Received) };
            Check(MessageClipboard.TryCreateDataObject(clipMessages, [], out var orderedData) && MessageClipboard.TryReadOrdered(orderedData!, out var clipParts) && clipParts.Length == 3, "mixed copy includes a bounded ordered payload alongside native formats");
            input.SelectAll(); Wait(window.StageComposerDataAsync(orderedData!));
            Check(window.ReadComposer().Select(p => p.Text ?? p.File!.Name).SequenceEqual(new[] { "before", "report.pdf", "after 🌍" }), "mixed text/file/text paste preserves order and actual inline attachment");
            Check(input.Selection.IsEmpty, "mixed paste leaves only a caret");
            var malformed = new DataObject(); malformed.SetData(MessageClipboard.OrderedFormat, "[null]");
            Check(!MessageClipboard.TryReadOrdered(malformed, out _), "invalid clipboard payload is rejected");
            malformed.SetData(MessageClipboard.OrderedFormat, "[{\"Path\":\"relative.pdf\"}]");
            Check(!MessageClipboard.TryReadOrdered(malformed, out _), "private clipboard paths must be fully qualified");
            malformed.SetText("recoverable text"); input.SelectAll(); Wait(window.StageComposerDataAsync(malformed));
            Check(window.ReadComposer().Single().Text == "recoverable text", "invalid private clipboard preserves native text fallback");
            input.SelectAll();
            Check(MessageClipboard.TryCreateDataObject([clipMessages[1]], [], out var onlyFile), "pure file payload created");
            Wait(window.StageComposerDataAsync(onlyFile!));
            Check(window.ReadComposer().Count == 1 && window.ReadComposer()[0].File is not null, "pure attachment paste does not duplicate text filename fallback");
            input.SelectAll();
            var externalMixed = new DataObject(); externalMixed.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath(source) }); externalMixed.SetText("external text");
            Wait(window.StageComposerDataAsync(externalMixed));
            Check(window.ReadComposer().Count == 2 && window.ReadComposer()[0].Text == "external text" && window.ReadComposer()[1].File is not null, "external clipboard text and files both survive");

            var paragraph = new Paragraph(new Run("text1text2")) { Margin = new Thickness(0) }; input.Document = new FlowDocument(paragraph);
            var run = (Run)paragraph.Inlines.FirstInline!; input.CaretPosition = run.ContentStart.GetPositionAtOffset(5)!;
            window.InsertComposerAttachment(file);
            Check(window.ReadComposer().Select(p => p.Text ?? p.File!.Name).SequenceEqual(new[] { "text1", "report.pdf", "text2" }), "real WPF rich editor inserts at caret");
            Check(window.ViewModel.AllTransfers.All(t => t.Id != file.Id), "staging does not create transfer");
            input.ApplyTemplate(); input.Measure(new Size(700, 160)); input.Arrange(new Rect(0, 0, 700, 160)); input.UpdateLayout();
            EditingCommands.Backspace.Execute(null, input);
            Check(window.ReadComposer().All(p => p.File is null) && string.Concat(window.ReadComposer().Select(p => p.Text)) == "text1text2", "native backspace removes only inline attachment");
            input.Undo();
            Check(window.ReadComposer().Count(p => p.File is not null) == 1, "native undo restores attachment metadata");
            var data = new DataObject(); var bitmap = new BitmapImage(new Uri(Path.GetFullPath("design/brand/final/bluelink-final-logo.png"))); bitmap.Freeze(); data.SetData(DataFormats.Bitmap, bitmap);
            input.CaretPosition = input.Document.ContentEnd; Wait(window.StageComposerDataAsync(data));
            Check(window.ReadComposer().Count(p => p.File is not null) == 2, "clipboard bitmap becomes inline image");
            var image = window.ReadComposer().Last().File!; Check(File.Exists(image.Path) && image.Size > 0 && image.Name.EndsWith(".png"), "clipboard image persisted with size");
            input.CaretPosition = input.Document.ContentEnd;
            EditingCommands.Backspace.Execute(null, input);
            Check(window.ReadComposer().Count(p => p.File is not null) == 1, "native backspace removes image without serialization failures");
            Check(input.Undo() && window.ReadComposer().Last().File?.Id == image.Id, "native undo restores image and metadata");
            var root = (FrameworkElement)window.Content; window.Content = null; root.DataContext = window.ViewModel; root.Resources.MergedDictionaries.Add(app.Resources); root.Resources.MergedDictionaries.Add(window.Resources); root.SetValue(TextElement.FontFamilyProperty, window.FontFamily); root.SetResourceReference(TextElement.ForegroundProperty, "InkBrush"); if (NameScope.GetNameScope(window) is { } names) NameScope.SetNameScope(root, names);
            foreach (var height in new[] { 120d, 240d })
            foreach (var theme in new[] { "light", "dark" })
            {
                typeof(MainWindow).GetField("_composerPreferred", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, height);
                typeof(MainWindow).GetMethod("ApplyComposerHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme }));
                root.Measure(new Size(1000, 720)); root.Arrange(new Rect(0, 0, 1000, 720)); root.UpdateLayout();
                var cards = input.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Select(i => (System.Windows.Controls.Border)i.Child).ToArray();
                Check(cards.All(c => !((Grid)c.Child).Children.OfType<System.Windows.Controls.Primitives.ButtonBase>().Any()), "no attachment delete buttons");
                Check(cards.All(c => c.ActualHeight <= input.ActualHeight && c.ActualWidth <= 220), "compact cards fit editor at " + height + theme);
                var render = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32); render.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render)); using var output = File.Create(Path.Combine(directory, "composer-" + theme + "-" + height + ".png")); encoder.Save(output);
                Check(input.ActualHeight > 20, "editor visible in " + theme);
            }
            input.Document = new FlowDocument(new Paragraph());
            foreach (var dimensions in new[] { (2000, 10), (10, 2000), (200, 200), (1200, 800), (800, 1200), (10, 10) })
            {
                var path = Path.Combine(directory, $"image-{dimensions.Item1}x{dimensions.Item2}.png");
                var fixture = new DrawingVisual(); using (var dc = fixture.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, dimensions.Item1, dimensions.Item2));
                    dc.DrawRectangle(Brushes.MediumSeaGreen, null, new Rect(dimensions.Item1 * .25, dimensions.Item2 * .25, dimensions.Item1 * .5, dimensions.Item2 * .5));
                }
                var pixels = new RenderTargetBitmap(dimensions.Item1, dimensions.Item2, 96, 96, PixelFormats.Pbgra32); pixels.Render(fixture);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pixels)); using (var output = File.Create(path)) png.Save(output);
                input.CaretPosition = input.Document.ContentEnd;
                window.InsertComposerAttachment(new(Guid.NewGuid(), path, Path.GetFileName(path), new FileInfo(path).Length));
            }
            root.Measure(new Size(1000, 720)); root.Arrange(new Rect(0, 0, 1000, 720)); root.UpdateLayout();
            var thumbnails = input.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Select(i => ((Grid)((Border)i.Child).Child).Children.OfType<Image>().Single()).ToArray();
            Check(thumbnails[0].Width == 96 && thumbnails[0].Height == 48 && thumbnails[1].Width == 48 && thumbnails[1].Height == 64, "actual extreme thumbnails use shared crop viewport");
            Check(thumbnails[2].Width == thumbnails[2].Height, "actual square thumbnail has no fixed landscape frame");
            var gallery = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32); gallery.Render(root);
            var galleryPng = new PngBitmapEncoder(); galleryPng.Frames.Add(BitmapFrame.Create(gallery)); using (var output = File.Create(Path.Combine(directory, "composer-extreme-gallery.png"))) galleryPng.Save(output);
            ComposerImageVerification.Run(window, input, root, directory, Check);
            ComposerShortcutVerification.Run(window, input, directory, Check, Wait);
            Wait(window.ViewModel.ClearConversationAsync(peer));
            Check(window.ReadComposer().Count == 0 && window.ViewModel.ComposerStore.Get(peer).Count == 0, "privacy clear removes rich draft and editor");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "no desktop window created");
        }
        finally { Wait(window.DisposeAsync().AsTask()); app.Shutdown(); }
        Console.WriteLine($"Composer verification passed: {checks} checks, 31 offscreen images, no desktop window or system clipboard access");
    }
}
