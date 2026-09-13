using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Domain;

internal static partial class BackgroundSettingsVerification
{
    private static void VerifyInputs(MainWindow window, string output, Action<bool, string> check)
    {
        var metrics = new List<string>();
        var failures = new List<string>();
        void Record(bool value, string label) { if (!value) failures.Add(label); else check(true, label); }
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var width in new[] { 1000, 1260 })
            {
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = language }));
                window.Width = width; window.Height = 600; Drain();
                var prefix = $"{theme}-{language}-{width}";
                window.OpenFileWorkspace(allDevices: true); Drain();
                Inspect(window, prefix + "-files");
                window.OpenSettings(); Drain();
                var page = window.ActiveSettingsPage!;
                foreach (var tab in new[] { "General", "Connection", "Files", "Privacy" })
                {
                    Invoke((FrameworkElement)page.FindName(tab + "NavigationItem")); Drain();
                    Inspect(page, prefix + "-" + tab);
                }
                foreach (var title in new[] { "开源许可", "帮助与反馈" })
                {
                    Invoke((FrameworkElement)page.FindName("AboutNavigationItem"));
                    Invoke(Descendants<Wpf.Ui.Controls.Button>(page).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == BlueLink.Localization.Strings.Get(title)));
                    Inspect(page, prefix + "-" + title);
                }
                // Discard QA-only path/limit/search edits instead of saving them.
                Invoke((FrameworkElement)page.FindName("CancelButton")); Drain();
                if (window.ActiveSettingsPage != null) window.TryCloseSettings();

                foreach (var dialog in new Window[] {
                    new DeviceNameWindow(window.ViewModel),
                    new MessageSearchWindow("QA 设备", new ObservableCollection<ChatItem>()) })
                {
                    // Render these children without Show(), so their Loaded focus handlers cannot activate them.
                    try
                    {
                        var content = (FrameworkElement)dialog.Content!;
                        dialog.Content = null;
                        var host = new Border { Resources = dialog.Resources, Child = content, Width = dialog.Width, Height = dialog.Height };
                        host.Measure(new Size(host.Width, host.Height)); host.Arrange(new Rect(0, 0, host.Width, host.Height)); host.UpdateLayout();
                        Inspect(host, prefix + "-" + dialog.GetType().Name, detached: true);
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "input-metrics.tsv"), metrics);
            File.WriteAllLines(Path.Combine(output, "input-failures.txt"), failures);
        }
        check(failures.Count == 0, "all input geometry checks pass: " + String.Join("; ", failures.Take(6)));
        VerifySettingsReview(window, output, check);

        void Inspect(FrameworkElement scope, string scenario, bool detached = false)
        {
            foreach (var control in Descendants<Control>(scope).Where(c => c is TextBox || c is ComboBox).ToArray())
            {
                if (control.Visibility != Visibility.Visible || (!detached && !control.IsVisible) || control.ActualHeight <= 0) continue;
                if (control is TextBox box)
                {
                    if (box.AcceptsReturn) continue;
                    var original = box.Text;
                    try
                    {
                        foreach (var value in new[] { "", "每次询问 Agjp 0123", @"D:\BlueLink\中文路径\abcdefghijklmnopqrstuvwxyz\下载目录" })
                        {
                            if (detached) box.Text = value;
                            else ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(box)!.GetPattern(PatternInterface.Value)).SetValue(value);
                            scope.UpdateLayout(); Drain();
                            InputControlGeometry.Verify(box, Record, metrics, scenario);
                        }
                        box.IsEnabled = false; scope.UpdateLayout();
                        InputControlGeometry.Verify(box, Record, metrics, scenario + "-disabled");
                    }
                    finally { box.IsEnabled = true; box.Text = original; scope.UpdateLayout(); Drain(); }
                }
                else if (control is ComboBox combo)
                {
                    InputControlGeometry.Verify(combo, Record, metrics, scenario);
                    if (detached) continue;
                    var provider = (IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(combo)!.GetPattern(PatternInterface.ExpandCollapse);
                    provider.Expand(); Settle(); scope.UpdateLayout();
                    try
                    {
                        for (var index = 0; index < combo.Items.Count; index++)
                        {
                            if (combo.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item && item.IsVisible)
                                InputControlGeometry.Verify(item, Record, metrics, scenario + "/" + combo.Name + "/option-" + index);
                        }
                        if (languageForCapture(scenario))
                        {
                            var first = combo.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem;
                            var popupRoot = first == null ? null : PresentationSource.FromVisual(first)?.RootVisual as FrameworkElement;
                            Record(popupRoot != null && popupRoot != PresentationSource.FromVisual(combo)?.RootVisual,
                                "expanded selector has a separate native popup surface: " + scenario + "/" + combo.Name);
                            if (popupRoot != null) Save(popupRoot, scenario + "-" + combo.Name + "-menu");
                        }
                    }
                    finally { provider.Collapse(); Drain(); }
                }
                if (languageForCapture(scenario)) Save(control, scenario + "-" + control.Name);
            }
        }
        bool languageForCapture(string label) => label.Contains("zh-CN");
        void Save(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            // Render the stable visual root before cropping. A control or animated
            // PopupRoot rendered by itself can retain parent offsets/animation bounds.
            FrameworkElement visualRoot = element;
            while (VisualTreeHelper.GetParent(visualRoot) is FrameworkElement parent && parent is not Window)
                visualRoot = parent;
            var origin = element.TranslatePoint(new Point(), visualRoot);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visualRoot.ActualWidth * scale), (int)Math.Ceiling(visualRoot.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(visualRoot);
                var x = Math.Max(0, (int)Math.Floor(origin.X * scale));
                var y = Math.Max(0, (int)Math.Floor(origin.Y * scale));
                var width = Math.Min(bitmap.PixelWidth - x, (int)Math.Ceiling(element.ActualWidth * scale));
                var height = Math.Min(bitmap.PixelHeight - y, (int)Math.Ceiling(element.ActualHeight * scale));
                var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
                using var stream = File.Create(Path.Combine(output, name + $"-{scale:0.##}x.png")); encoder.Save(stream);
            }
        }
    }
}
