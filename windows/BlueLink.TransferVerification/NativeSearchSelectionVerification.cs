using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;

internal static class NativeSearchSelectionVerification
{
    internal static void Run(string directory, bool child = false)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        if (!child) { NativeDatePopupVerification.LaunchPrivateDesktop(directory, "--native-search-selection-child", "native-search-selection.log"); return; }
        if (!NativeDatePopupVerification.IsPrivateDesktop) throw new InvalidOperationException("Selection QA requires a private non-input desktop");
        var log = new List<string>(); Exception? failure = null;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); log.Add("PASS " + label); }
        var thread = new Thread(() =>
        {
            var app = App.CreateResourceOnlyHost(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                foreach (var theme in new[] { "light", "dark" })
                {
                    var main = new MainWindow(false, Path.Combine(directory, theme + "-data")) { ShowInTaskbar = false };
                    try
                    {
                        Wait(DesktopAcceptance.InitializeAsync(main, Path.Combine(directory, theme + "-data"), "message-history"));
                        Wait(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
                        var message = new ChatItem(Guid.NewGuid(), "BlueLink QA needle Windows to Android", true, DateTimeOffset.Now, MessageStatus.Delivered);
                        main.ViewModel.Messages.Clear(); main.ViewModel.Messages.Add(message);
                        main.Show(); Drain();
                        var chat = Children<TextBox>(main).Single(t => t.Name == "MessageText" && t.Text == message.Text);
                        chat.Focus(); Keyboard.Focus(chat);
                        ((ITextProvider)new TextBoxAutomationPeer(chat).GetPattern(PatternInterface.Text)).DocumentRange.FindText("BlueLink", false, false).Select(); Drain();
                        Check(chat.SelectedText == "BlueLink", "native chat UIA selection " + theme);
                        Capture((FrameworkElement)main.Content, Path.Combine(directory, "chat-selected-" + theme + ".png"));
                        var search = new MessageSearchWindow("QA 手机", new ObservableCollection<ChatItem>([message]), true, main.ViewModel) { ShowInTaskbar = false };
                        try
                        {
                            search.Show(); Drain(); search.ApplyFilter("needle", HistoryKind.All); Drain();
                            var text = Children<RichTextBox>(search).Single(t => t.Name == "SearchSelectableText");
                            text.Focus(); Keyboard.Focus(text); Drain();
                            var range = ((ITextProvider)new RichTextBoxAutomationPeer(text).GetPattern(PatternInterface.Text)).DocumentRange;
                            foreach (var selected in new[] { "BlueLink", "nee", "needle", "QA needle Windows" })
                            {
                                range.FindText(selected, false, false).Select(); Drain();
                                Check(text.Selection.Text == selected && search.ResultTextToCopy(message) == selected, "native search selection and Copy: " + selected + " / " + theme);
                                Check(text.Selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush fill && fill.Color == ((SolidColorBrush)text.SelectionBrush).Color &&
                                    text.Selection.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush ink && ink.Color == ((SolidColorBrush)text.SelectionTextBrush).Color,
                                    "selected range overrides match fill and ink: " + selected + " / " + theme);
                                Capture((FrameworkElement)search.Content, Path.Combine(directory, "search-selected-" + selected.Replace(' ', '-') + "-" + theme + ".png"));
                            }
                            var card = Children<Border>(search).Single(b => b.Name == "ResultCard");
                            NativeDatePopupVerification.SendMouse(text, 0x0204, 2); Drain();
                            NativeDatePopupVerification.SendMouse(text, 0x0205, 0); Drain();
                            var menu = (ContextMenu?)typeof(MessageSearchWindow).GetField("_resultMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search);
                            log.Add($"native mouse messages: menu={menu?.IsOpen}, selection={text.Selection.Text}");
                            if (menu?.IsOpen != true)
                            {
                                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
                                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent }); Drain();
                                menu = (ContextMenu?)typeof(MessageSearchWindow).GetField("_resultMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search);
                            }
                            Check(menu?.IsOpen == true && text.Selection.Text == "QA needle Windows", "routed right-click opens native popup and preserves selected substring " + theme);
                            menu!.IsOpen = false; text.Selection.Select(text.Document.ContentStart, text.Document.ContentStart); Drain();
                            Check(text.Selection.IsEmpty && search.ResultTextToCopy(message) == message.Text, "clearing selection restores whole-message Copy " + theme);
                            Check(text.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Any(run => run.Text == "needle" && run.Background is not null), "gold search match remains after clearing selection " + theme);
                            Capture((FrameworkElement)search.Content, Path.Combine(directory, "search-selection-cleared-" + theme + ".png"));
                        }
                        finally { search.Close(); }
                    }
                    finally { main.Close(); Wait(main.DisposeAsync().AsTask()); }
                }
            }
            catch (Exception e) { failure = e; log.Add(e.ToString()); }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        File.WriteAllLines(Path.Combine(directory, "native-search-selection.log"), log);
        if (failure is not null) throw new InvalidOperationException("Native selection verification failed", failure);
    }
    private static void Wait(Task task) { while (!task.IsCompleted) Drain(); task.GetAwaiter().GetResult(); }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); if (child is T value) yield return value;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
