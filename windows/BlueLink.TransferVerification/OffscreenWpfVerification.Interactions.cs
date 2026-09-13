using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Storage;
using UiButton = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyControlInteractions(string dataRoot, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            var directory = Path.Combine(dataRoot, "interactions-" + theme);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, "files-current"));
                WaitForUiTask(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
                var root = DetachForRendering(window);
                var list = (ListBox)window.FindName("TransferList");
                foreach (var height in new[] { 1000, 600, 1000 })
                {
                    var scene = $"interactions-files-{theme}-{height}";
                    list.MaxHeight = height == 600 ? 200 : double.PositiveInfinity;
                    Capture(root, output, scene, 1180, height);
                    var scroll = Descendants<ScrollViewer>(list).First();
                    var viewport = Descendants<ScrollContentPresenter>(scroll).First();
                    var hasBar = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible;
                    Check(hasBar == (height == 600), "file scrollbar follows content overflow: " + scene);
                    Check(scroll.Padding.Right == (hasBar ? 14 : 0), "file scrollbar lane exists only on overflow: " + scene);
                    Check(Math.Abs(viewport.ActualWidth - (scroll.ActualWidth - (hasBar ? 14 : 0))) < 2,
                        "file viewport uses every available horizontal pixel: " + scene);
                    var background = (Border)window.FindName("FileColumnsHeaderBackground");
                    Check(background.Margin == new Thickness(0) && Math.Abs(background.ActualWidth - list.ActualWidth) < 1,
                        "file header background fills the entire table including both edges: " + scene);
                    var header = (Grid)window.FindName("FileColumnsHeader");
                    var row = Descendants<Border>(list).First(b => b.Name == "FileTableRow");
                    Check(Math.Abs(header.ActualWidth - ((Grid)row.Child).ActualWidth) < 2,
                        "file heading and row columns stay aligned after scrollbar transitions: " + scene);
                }
                VerifyFocusAndPointer(root, "files-" + theme);
                var captions = Descendants<Wpf.Ui.Controls.TitleBarButton>(root).Where(button => Displayed(button, root)).ToArray();
                Check(captions.Length == 3, "caption regression covers minimize, maximize and close: " + theme);
                foreach (var activeTheme in new[] { theme == "light" ? "dark" : "light", theme })
                {
                    AppearanceService.Apply(window.ViewModel.Settings with { Theme = activeTheme });
                    Layout(root, 1180, 1000);
                    var ink = (SolidColorBrush)root.FindResource("InkBrush");
                    Check(captions.All(button => button.RenderButtonsForeground is SolidColorBrush brush && brush.Color == ink.Color),
                        "existing caption glyphs follow a live theme switch: " + activeTheme);
                }
                foreach (var caption in captions)
                {
                    caption.Hover();
                    Layout(root, 1180, 1000);
                    var expected = (SolidColorBrush)(caption.MouseOverButtonsForeground ?? caption.ButtonsForeground);
                    Check(caption.RenderButtonsForeground is SolidColorBrush ink && ink.Color == expected.Color,
                        "caption hover retains readable ink: " + theme + "/" + caption.ButtonType);
                    if (caption.ButtonType == Wpf.Ui.Controls.TitleBarButtonType.Close)
                        Capture(root, output, "interactions-caption-hover-" + theme, 1180, 1000);
                    caption.RemoveHover();
                }
                Capture(root, output, "interactions-caption-restored-" + theme, 1180, 1000);
                var searchHost = (Border)window.FindName("FileSearchHost");
                SetInteractionState(searchHost, "IsKeyboardFocusWithin", true);
                Check(searchHost.BorderThickness == new Thickness(1.5),
                    "file search preserves the requested input focus feedback: " + theme);
                SetInteractionState(searchHost, "IsKeyboardFocusWithin", false);
                var links = Descendants<UiButton>(root).Where(button =>
                    ReferenceEquals(button.Style, root.FindResource("LinkButtonStyle")) && Displayed(button, root)).ToArray();
                Check(links.Any(button => button.Name == "ChangeReceiveDirectoryButton") &&
                      links.Any(button => button.Content?.ToString() == "打开"), "file link acceptance covers open and change: " + theme);
                foreach (var button in links)
                {
                    SetInteractionState(button, "IsMouseOver", true);
                    var border = (Border)button.Template.FindName("ContentBorder", button);
                    Check(border.Background is SolidColorBrush { Color.A: 0 } && border.BorderThickness == new Thickness(0),
                        "text link has no hover fill or border: " + theme + "/" + button.Content);
                    if (button.Name == "ChangeReceiveDirectoryButton")
                        Capture(root, output, "interactions-link-hover-" + theme, 1180, 1000);
                    SetInteractionState(button, "IsMouseOver", false);
                }
                var query = (Wpf.Ui.Controls.TextBox)window.FindName("FileSearchInput");
                list.MaxHeight = 200;
                Layout(root, 1180, 600);
                query.Text = "no-matching-file";
                Capture(root, output, "interactions-empty-" + theme, 1180, 600);
                Check(Descendants<ScrollViewer>(list).First().Padding.Right == 0,
                    "filtering to no results immediately releases the scrollbar lane: " + theme);
                query.Clear();
                Layout(root, 1180, 600);
                Check(Descendants<ScrollViewer>(list).First().Padding.Right == 14,
                    "restoring results restores only the required scrollbar lane: " + theme);
                using var settings = new SettingsPage(window.ViewModel);
                var settingsRoot = DetachForRendering(settings);
                Capture(settingsRoot, output, "interactions-settings-" + theme, 1180, 720);
                VerifyFocusAndPointer(settingsRoot, "settings-" + theme);
                var nav = (Wpf.Ui.Controls.NavigationView)settings.FindName("SettingsNavigation");
                var separator = (Border)nav.Template.FindName("PART_FooterSeparator", nav);
                Check(separator.Background is SolidColorBrush { Color.A: 0 },
                    "settings sidebar has no empty footer separator: " + theme);
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
        AppearanceService.Apply(BlueLinkSettings.Defaults(dataRoot) with { Theme = "light", Language = "zh-CN" });
    }

    private void VerifyFocusAndPointer(FrameworkElement root, string scene)
    {
        foreach (var control in Descendants<Control>(root).Where(control => Displayed(control, root)).ToArray())
        {
            var label = scene + "/" + control.GetType().Name + "/" + control.Name;
            Check(control.FocusVisualStyle is null, "no focus adorner: " + label);
            if (control is TextBox input)
            {
                var caret = input.CaretBrush;
                SetInteractionState(input, "IsFocused", true);
                Check(input.CaretBrush == caret && input.Focusable &&
                    input.Template.FindName("AccentBorder", input) is Border { BorderThickness.Bottom: 2 },
                    "text input keeps its official focus feedback and caret: " + label);
                SetInteractionState(input, "IsFocused", false);
                continue;
            }
            if (control is ComboBox) continue;
            var before = InteractionVisualState(control);
            var focusable = control.Focusable;
            foreach (var state in new[] { "IsFocused", "IsKeyboardFocused", "IsKeyboardFocusWithin" })
                SetInteractionState(control, state, true);
            Check(InteractionVisualState(control) == before && control.Focusable == focusable,
                "focus changes no presentation or keyboard eligibility: " + label);
            foreach (var state in new[] { "IsFocused", "IsKeyboardFocused", "IsKeyboardFocusWithin" })
                SetInteractionState(control, state, false);
            if (control is ButtonBase or MenuItem && control.IsEnabled)
            {
                var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.QueryCursorEvent };
                control.RaiseEvent(query);
                Check(query.Cursor == Cursors.Hand, "all button pointer queries return a hand: " + label);
            }
        }
    }

    private static void SetInteractionState(UIElement element, string name, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField(name + "PropertyKey",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        element.SetValue(key, value);
    }

    private static string InteractionVisualState(Control control) =>
        $"{control.Background}|{control.BorderBrush}|{control.BorderThickness}|{control.Foreground}|" +
        string.Join(";", Descendants<Border>(control).Select(border =>
            $"{border.Name}:{border.Background}:{border.BorderBrush}:{border.BorderThickness}:{border.Visibility}"));
}
