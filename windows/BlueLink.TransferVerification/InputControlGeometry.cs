using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// Shared by client and installer UI-only verification; measures the rendered
// template content rather than assuming Height/VerticalContentAlignment suffice.
internal static class InputControlGeometry
{
    internal static void Verify(Control control, Action<bool, string> check, ICollection<string> metrics, string scenario)
    {
        control.UpdateLayout();
        var label = scenario + "/" + control.Name;
        if (control is TextBox box)
        {
            if (box.AcceptsReturn) return; // Multiline editors intentionally start at the top.
            var host = box.Template.FindName("PART_ContentHost", box) as ScrollViewer;
            check(host != null, "input has an actual text viewport: " + label);
            if (host == null) return;
            if (box.Text.Length > 0)
            {
                var line = box.GetRectFromCharacterIndex(0);
                var top = host.TranslatePoint(new Point(), box).Y;
                metrics.Add($"{label}\ttext\tcontrol={box.ActualHeight:F3}\tviewport={host.ActualHeight:F3}\tline={line}\tpadding={box.Padding}");
                check(!line.IsEmpty && line.Height > 0, "input line has measurable glyph bounds: " + label);
                if (line.IsEmpty) return;
                check(line.Top >= top - .8 && line.Bottom <= top + host.ActualHeight + .8,
                    "input text fits its internal viewport: " + label);
                check(Math.Abs(line.Top + line.Height / 2 - box.ActualHeight / 2) <= 1.2,
                    "input text is vertically centered: " + label);
                check(host.ScrollableHeight < .8, "single-line input has no vertical overflow: " + label);
            }
            else
            {
                var placeholder = box.Template.FindName("PlaceholderTextBox", box) as TextBlock;
                if (placeholder != null && placeholder.IsVisible && placeholder.Text.Length > 0)
                    VerifyLabel(placeholder, box, check, metrics, label + "/placeholder");
            }
        }
        else if (control is ComboBox || control is ComboBoxItem)
        {
            var labels = Descendants<TextBlock>(control).Where(t => t.IsVisible && !String.IsNullOrWhiteSpace(t.Text)).ToArray();
            check(labels.Length > 0, "selector contains rendered text: " + label);
            foreach (var text in labels) VerifyLabel(text, control, check, metrics, label);
        }
    }

    private static void VerifyLabel(TextBlock text, Control control, Action<bool, string> check, ICollection<string> metrics, string label)
    {
        var natural = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var top = text.TranslatePoint(new Point(), control).Y;
        metrics.Add($"{label}\t{text.Text}\tcontrol={control.ActualHeight:F3}\tactual={text.ActualHeight:F3}\tnatural={natural.Height:F3}\ttop={top:F3}\tpadding={control.Padding}");
        check(text.ActualHeight + .8 >= natural.Height, "selector/placeholder retains full line height: " + label + "/" + text.Text);
        check(top >= -.8 && top + natural.Height <= control.ActualHeight + .8,
            "selector/placeholder text stays inside control: " + label + "/" + text.Text);
        check(Math.Abs(top + text.ActualHeight / 2 - SurfaceCenter(control)) <= 1.2,
            "selector/placeholder text is vertically centered: " + label + "/" + text.Text);
        // A full TextBlock can still be clipped by its smaller template presenter.
        for (var parent = VisualTreeHelper.GetParent(text); parent != null && parent != control; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement element)
            {
                var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element);
                var y = text.TranslatePoint(new Point(), element).Y;
                if (element.ClipToBounds)
                    check(y >= -.8 && y + natural.Height <= element.ActualHeight + .8,
                        "template bounds preserve the complete text line: " + label + "/" + element.GetType().Name);
                if (clip != null)
                    check(y >= clip.Bounds.Top - .8 && y + natural.Height <= clip.Bounds.Bottom + .8,
                        "template layout clip preserves the complete text line: " + label + "/" + element.GetType().Name);
            }
        }
    }

    private static double SurfaceCenter(Control control)
    {
        // Official ComboBoxItem includes a 4 DIP gap above its painted surface.
        // Center against the actual highlight, not that inter-item spacing.
        var surface = control is ComboBoxItem ? control.Template.FindName("ContentBorder", control) as FrameworkElement : null;
        return surface == null ? control.ActualHeight / 2 : surface.TranslatePoint(new Point(0, surface.ActualHeight / 2), control).Y;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
