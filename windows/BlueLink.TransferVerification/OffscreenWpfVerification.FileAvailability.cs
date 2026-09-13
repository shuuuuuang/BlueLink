using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;
using BlueLink.Storage;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyFileAvailabilityDialogs(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "file-availability");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "available.json");
        File.WriteAllText(path, "{\"qa\":true}");
        var available = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "域名台账.json",
            "application/json", new FileInfo(path).Length, path, "Completed");
        Check(FileInteractionService.TryGetLocalPath(available, out var resolved) && resolved == path,
            "file availability: completed local files remain usable");
        foreach (var state in new[] { "Offered", "Transferring", "Paused", "Verifying", "Committing", "Failed", "Canceled" })
            Check(!FileInteractionService.TryGetLocalPath(available with { State = state }, out _),
                "file availability: partial local file is blocked for every file action: " + state);
        foreach (var missing in new string?[] { null, "", " ", Path.Combine(directory, "moved.json") })
            Check(!FileInteractionService.TryGetLocalPath(available with { LocalPath = missing }, out _),
                "file availability: missing or blank local path cannot reach an external file action");
        File.Delete(path);
        Check(!FileInteractionService.TryGetLocalPath(available, out _),
            "file availability: file removal after a previous success is detected again");

        var originalLanguage = Strings.Language;
        var originalTheme = AppearanceService.CurrentTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark ? "dark" : "light";
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var theme in new[] { "light", "dark" })
            foreach (var width in new[] { 520, 360 })
            {
                AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Language = language, Theme = theme });
                var attachment = width == 520 ? available : available with {
                    FileName = "蓝联跨设备文件传输验收报告_2026年09月07日_含测试结果与附件说明_最终确认版本.pdf",
                    MimeType = "application/pdf" };
                var window = new FileAvailabilityWindow(attachment);
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.ConstrainTo(new Size(width + 48, 600));
                var root = DetachForRendering(window);
                DrainDispatcher();
                root.Measure(new Size(width, double.PositiveInfinity));
                var height = (int)Math.Ceiling(Math.Max(window.MinHeight, root.DesiredSize.Height));
                var scene = $"file-unavailable-{language}-{theme}-{width}";
                Capture(root, output, scene, width, height);
                var name = (TextBlock)window.FindName("UnavailableFileName");
                var card = (Border)window.FindName("UnavailableFileCard");
                var primary = (Button)window.FindName("FileAvailabilityAcknowledgeButton");
                var close = (Button)window.FindName("FileAvailabilityCloseButton");
                var footer = (Border)window.FindName("FileAvailabilityFooter");
                var explanation = (TextBlock)window.FindName("FileAvailabilityExplanation");
                var title = (TextBlock)window.FindName("FileAvailabilityTitle");
                var icon = (Image)window.FindName("UnavailableFileIcon");
                Check(name.Text == attachment.FileName && name.ToolTip as string == attachment.FileName &&
                    name.TextWrapping == TextWrapping.Wrap && name.TextTrimming == TextTrimming.None,
                    "file availability: actual filename is preserved and can wrap: " + scene);
                Check(icon.Source is not null && icon.Source.ToString()!.EndsWith(width == 520 ? "file-document.png" : "file-pdf.png") &&
                    icon.ActualWidth == 24 && icon.ActualHeight == 24,
                    "file availability: actual file type selects the existing Figma icon: " + scene);
                var filenameBounds = name.TransformToAncestor(card).TransformBounds(new Rect(name.RenderSize));
                Check(filenameBounds.Right <= card.ActualWidth - 9 && filenameBounds.Top >= 9 &&
                    filenameBounds.Bottom <= card.ActualHeight - 9 && (width == 520 || name.ActualHeight > 22),
                    "file availability: long filename grows inside the file card: " + scene);
                Check(Descendants<Button>(root).Count() == 2 && primary.Content as string == Strings.Get("知道了") &&
                    primary.IsDefault && primary.IsEnabled && close.IsEnabled &&
                    primary.ActualWidth == 88 && primary.ActualHeight == 36 && close.ActualWidth == 32,
                    "file availability: only a close icon and one enabled acknowledgement, no empty action: " + scene);
                var primaryBounds = primary.TransformToAncestor(root).TransformBounds(new Rect(primary.RenderSize));
                var titleBounds = title.TransformToAncestor(root).TransformBounds(new Rect(title.RenderSize));
                var closeBounds = close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize));
                var explanationBounds = explanation.TransformToAncestor(root).TransformBounds(new Rect(explanation.RenderSize));
                Check(Math.Abs(width - primaryBounds.Right - 24) <= 1 && primaryBounds.Bottom < height &&
                    titleBounds.Right < closeBounds.Left &&
                    explanationBounds.Bottom <= footer.TranslatePoint(new Point(), root).Y - 12,
                    "file availability: title, body, close control and footer have separate bounds: " + scene);
                Check(explanation.Text.Contains(language == "zh-CN" ? "请检查传输状态和保存目录后重试。" : "Check the transfer status") &&
                    title.Text == (language == "zh-CN" ? "无法打开文件" : "Cannot open file") &&
                    ((TextBlock)window.FindName("UnavailableFileStatus")).Text == Strings.Get("本地文件不可用"),
                    "file availability: all captions follow the current language: " + scene);
                Check(((SolidColorBrush)primary.Background).Color == ((SolidColorBrush)root.FindResource("BlueBrush")).Color &&
                    ((SolidColorBrush)card.Background).Color == ((SolidColorBrush)root.FindResource("CanvasBrush")).Color &&
                    name.FontFamily.Source.Contains("Noto Sans SC"),
                    "file availability: existing theme colors and approved font are used: " + scene);
                if (language == "zh-CN" && width == 520)
                    Check(Math.Abs(height - 306) <= 2, "file availability: standard Chinese content matches 520 by 306 prototype: " + theme);
                var commandButton = width == 520 ? primary : close;
                ((RoutedCommand)commandButton.Command).Execute(null, commandButton);
                Check(closed && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                    "file availability: real acknowledgement/close command closes without native activation: " + scene);
            }

            AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Language = "zh-CN", Theme = "light" });
            var constrained = new FileAvailabilityWindow(available with { FileName = string.Concat(Enumerable.Repeat("超长文件名_", 100)) + ".json" });
            constrained.ConstrainTo(new Size(408, 328));
            var constrainedRoot = DetachForRendering(constrained);
            Capture(constrainedRoot, output, "file-unavailable-scroll", 360, 280);
            var scroll = (ScrollViewer)constrained.FindName("FileAvailabilityScroll");
            var button = (Button)constrained.FindName("FileAvailabilityAcknowledgeButton");
            Check(constrained.Width == 360 && constrained.MaxHeight == 280 && constrained.MinHeight == 280 &&
                scroll.ScrollableHeight > 0 && scroll.Padding.Right == 14 &&
                button.TransformToAncestor(constrainedRoot).TransformBounds(new Rect(button.RenderSize)).Bottom <= 280,
                "file availability: limited owner height scrolls the body and keeps the only action visible");
            scroll.ScrollToEnd();
            Capture(constrainedRoot, output, "file-unavailable-scroll-bottom", 360, 280);
            var text = (TextBlock)constrained.FindName("FileAvailabilityExplanation");
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 &&
                text.TranslatePoint(new Point(0, text.ActualHeight), scroll).Y <= scroll.ActualHeight,
                "file availability: complete explanation remains reachable below a very long filename");
            var escape = constrained.InputBindings.OfType<KeyBinding>().Single();
            var escaped = false;
            constrained.Closed += (_, _) => escaped = true;
            Check(escape.Key == Key.Escape && escape.Command == ApplicationCommands.Close,
                "file availability: Escape maps to the same dismiss command");
            ((RoutedCommand)escape.Command).Execute(null, constrainedRoot);
            Check(escaped && new WindowInteropHelper(constrained).Handle == IntPtr.Zero,
                "file availability: Escape command closes without a desktop window");

            foreach (var (mime, iconName) in new[] { ("image/png", "file-image.png"), ("video/mp4", "file-video.png") })
            {
                var window = new FileAvailabilityWindow(available with { MimeType = mime });
                var root = DetachForRendering(window);
                Layout(root, 520, 306);
                Check(((Image)window.FindName("UnavailableFileIcon")).Source.ToString()!.EndsWith(iconName),
                    "file availability: image and video reuse their matching file icon: " + mime);
                window.Close();
            }
        }
        finally
        {
            AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Language = originalLanguage, Theme = originalTheme });
        }
    }
}
