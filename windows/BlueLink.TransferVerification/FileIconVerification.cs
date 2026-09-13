using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Storage;

internal static class FileIconVerification
{
    public static void Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "shared/file-icons/catalog.json"))) repo = repo.Parent;
        if (repo == null) throw new InvalidOperationException("File icon catalog not found.");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.FullName, "shared/file-icons/catalog.json")));
        var icons = catalog.RootElement.GetProperty("icons").EnumerateArray().Select(i => new
        {
            Id = i.GetProperty("id").GetString()!,
            Name = "sample." + (i.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()).FirstOrDefault() ?? "unknown"),
            Light = i.GetProperty("light").GetString()!,
            Dark = i.GetProperty("dark").GetString()!,
            Extensions = i.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()!).ToArray()
        }).ToArray();
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        foreach (var line in File.ReadLines(Path.Combine(repo.FullName, "shared/file-icons/tests/file-icon-cases.tsv")).Skip(1).Where(l => l.Length > 0))
        {
            var fields = line.Split('\t');
            Check(FileTypeCatalog.Classify(fields[0], fields[1]) == fields[2], "classification: " + fields[0]);
        }
        foreach (var icon in icons)
        foreach (var ext in icon.Extensions)
            Check(FileTypeCatalog.Classify("附件." + ext.ToUpperInvariant(), "application/octet-stream") == icon.Id, "extension: " + ext);
        Check(FileTypeCatalog.Classify(null, null) == "file", "null input fallback");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            Window? gallery = null;
            MainWindow? main = null;
            try
            {
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                main = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(output, "isolated-state"))
                {
                    Width = 1180, Height = 760, Left = SystemParameters.VirtualScreenLeft - 5000,
                    Top = SystemParameters.VirtualScreenTop - 5000, WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0
                };
                var root = new StackPanel { Margin = new Thickness(16) };
                root.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush");
                var tiles = new UniformGrid { Columns = 4 };
                root.Children.Add(tiles);
                var images = new Dictionary<string, Image>();
                foreach (var icon in icons)
                {
                    var column = new StackPanel { Margin = new Thickness(8), Height = 112 };
                    var label = new TextBlock { Text = icon.Name, FontSize = 13, Margin = new Thickness(0, 4, 0, 8) };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                    column.Children.Add(label);
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var size in new[] { 20, 24, 40 })
                    {
                        var image = new Image { Source = FileTypeIcons.ForFile(icon.Name), Width = size, Height = size, Margin = new Thickness(4) };
                        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                        AutomationProperties.SetName(image, icon.Name + "-" + size);
                        row.Children.Add(image);
                        if (size == 40) images[icon.Id] = image;
                    }
                    column.Children.Add(row);
                    tiles.Children.Add(column);
                }
                var template = (DataTemplate)main.FindResource("AttachmentTemplate");
                var attachments = new WrapPanel();
                foreach (var name in new[] { "report.docx", "schema.sql", "disk.qcow2", "settings.yaml" })
                {
                    var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), name, "application/octet-stream", 1024, State: "Completed");
                    var card = new ContentControl { Content = attachment, ContentTemplate = template, Margin = new Thickness(8), Width = 480 };
                    attachments.Children.Add(card);
                }
                root.Children.Add(attachments);
                gallery = new Window
                {
                    Title = "BlueLink file icon acceptance", Content = root, Width = 1080, Height = 960,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = SystemParameters.VirtualScreenLeft - 5000,
                    Top = SystemParameters.VirtualScreenTop - 5000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0
                };
                gallery.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
                gallery.Show(); Drain();
                Check(new WindowAutomationPeer(gallery).GetName() == gallery.Title, "native UI Automation host");
                Check(!gallery.IsActive && gallery.Left + gallery.ActualWidth < SystemParameters.VirtualScreenLeft, "background window does not take focus");
                var snapshots = new Dictionary<string, byte[]>();
                foreach (var theme in new[] { "light", "dark", "light" })
                {
                    AppearanceService.Apply(new BlueLinkSettings { Theme = theme });
                    Drain();
                    foreach (var icon in icons)
                    {
                        var image = images[icon.Id];
                        Check(image.Source != null && image.ActualWidth == 40 && image.ActualHeight == 40, theme + ": square icon " + icon.Id);
                        var drawing = (DrawingGroup)((DrawingImage)image.Source!).Drawing;
                        var color = ((SolidColorBrush)((GeometryDrawing)drawing.Children[0]).Brush).Color;
                        Check(color == (Color)ColorConverter.ConvertFromString(theme == "dark" ? icon.Dark : icon.Light), theme + ": live color " + icon.Id);
                        var mask = (BitmapSource)((ImageBrush)drawing.OpacityMask).ImageSource;
                        Check(mask.PixelWidth == 256 && mask.PixelHeight == 256, "source size: " + icon.Id);
                        var peer = new ImageAutomationPeer(image);
                        Check(peer.GetName() == icon.Name + "-40", "image UI Automation: " + icon.Id);
                        var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual();
                        using (var context = visual.RenderOpen()) context.DrawImage(image.Source, new Rect(0, 0, 40, 40));
                        bitmap.Render(visual);
                        var bytes = new byte[40 * 40 * 4]; bitmap.CopyPixels(bytes, 160, 0);
                        Check(bytes.Where((_, i) => i % 4 == 3).Any(a => a > 100), "icon is visible: " + icon.Id);
                        if (theme == "dark") Check(!bytes.SequenceEqual(snapshots[icon.Id]), "theme switch updates existing image: " + icon.Id);
                        else snapshots[icon.Id] = bytes;
                    }
                    var boundImages = Descendants<Image>(attachments).Where(i => i.DataContext is ChatAttachment && i.Width == 24).ToArray();
                    Check(boundImages.Length == 4 && boundImages.All(i => i.Source is DrawingImage), theme + ": actual attachment template resolves all icons");
                    Capture(root, Path.Combine(output, "icons-" + theme + ".png"));
                }
                foreach (var icon in icons)
                    main.ViewModel.AllTransfers.Add(new TransferItem { Id = Guid.NewGuid(), Name = icon.Name, TotalBytes = 1024, Outgoing = true, Status = TransferStatus.Completed });
                main.Show(); main.OpenFileWorkspace(allDevices: true); Drain();
                foreach (var theme in new[] { "light", "dark" })
                {
                    AppearanceService.Apply(new BlueLinkSettings { Theme = theme }); Drain();
                    var fileImages = Descendants<Image>(main).Where(i => i.DataContext is TransferItem && i.Width == 20).ToArray();
                    Check(fileImages.Length > 0 && fileImages.All(i => i.Source is DrawingImage), theme + ": actual file table icons");
                    Capture((FrameworkElement)main.Content, Path.Combine(output, "file-table-" + theme + ".png"));
                }
                Check(BlueLink.Legal.LicenseCatalog.All.Single(l => l.ResourceName == "PhosphorIcons.txt").Text.Contains("MIT"), "bundled Phosphor attribution");
            }
            catch (Exception e) { failure = e; }
            finally { main?.Close(); gallery?.Close(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("File icon rendering failed.", failure);
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"File icons: {checks.Count} checks passed; native WPF screenshots: {output}");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(FrameworkElement root, string path)
    {
        root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
