using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;

internal static class ComposerShortcutVerification
{
    public static void Run(MainWindow window, System.Windows.Controls.RichTextBox input,
        string directory, Action<bool, string> check, Action<Task> wait)
    {
        foreach (var mode in new[] { "enter", "ctrl-enter" })
        foreach (var key in new[] { Key.Enter, Key.A, Key.ImeProcessed, Key.System })
        foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control,
            ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt, ModifierKeys.Windows })
        foreach (var composing in new[] { false, true })
        {
            var expected = !composing && key == Key.Enter &&
                modifiers == (mode == "enter" ? ModifierKeys.None : ModifierKeys.Control);
            check(ComposerShortcuts.IsSendKey(mode, key, modifiers, composing) == expected,
                $"shortcut policy {mode}/{key}/{modifiers}/IME={composing}");
        }
        check(ComposerShortcuts.Normalize(null) == "enter" && ComposerShortcuts.Normalize("unknown") == "enter",
            "invalid settings preserve the default Enter behavior");

        var original = window.ViewModel.Settings;
        foreach (var mode in new[] { "enter", "ctrl-enter" })
        {
            wait(window.ViewModel.SaveSettingsAsync(original with { SendShortcut = mode }));
            check(window.IsComposerSendKey(Key.Enter, mode == "enter" ? ModifierKeys.None : ModifierKeys.Control),
                "window uses the saved shortcut");
            // Exercise the real XAML PreviewKeyDown subscription without an HWND or injected OS input.
            var modifiers = Keyboard.Modifiers;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenSource(), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            input.RaiseEvent(args);
            check(args.Handled == ComposerShortcuts.IsSendKey(mode, Key.Enter, modifiers, false),
                "preview routing consumes only the configured send key before RichTextBox edits");

            var composition = new TextComposition(InputManager.Current, input, "");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            check(!window.IsComposerSendKey(Key.Enter, ModifierKeys.None) && !window.IsComposerSendKey(Key.Enter, ModifierKeys.Control),
                "IME candidate confirmation is not sent");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            check(window.IsComposerSendKey(Key.Enter, mode == "enter" ? ModifierKeys.None : ModifierKeys.Control),
                "sending resumes after composition completion");
        }

        foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
        foreach (var theme in new[] { "light", "dark" })
        {
            wait(window.ViewModel.SaveSettingsAsync(original with { Language = language, Theme = theme, SendShortcut = "enter" }));
            using var page = new SettingsPage(window.ViewModel);
            var choice = (ComboBox)page.FindName("SendShortcutBox");
            check((string)choice.SelectedValue == "enter" && !page.HasUnsavedChanges, "settings start at persisted value");
            choice.SelectedValue = "ctrl-enter";
            check(page.HasUnsavedChanges, "shortcut participates in unsaved changes tracking");
            choice.SelectedValue = "enter";
            check(!page.HasUnsavedChanges, "reverting shortcut clears dirty state");
            choice.SelectedValue = "ctrl-enter";
            var save = (Task<bool>)typeof(SettingsPage).GetMethod("SaveDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
            wait(save);
            check(save.Result && !page.HasUnsavedChanges && window.ViewModel.Settings.SendShortcut == "ctrl-enter",
                "actual settings form saves shortcut");
            var reload = new BlueLinkDatabase(Path.Combine(directory, "ui")).LoadSettingsAsync(); wait(reload);
            check(reload.Result.SendShortcut == "ctrl-enter", "shortcut survives database reopen");
            // A fixture may have Bluetooth unavailable; when the composer hint is enabled it must use the setting.
            check(window.ViewModel.ComposerHint == BlueLink.Localization.Strings.Get(
                window.ViewModel.IsConnected || !window.ViewModel.IsBluetoothUnavailable
                    ? "Ctrl+Enter 发送 · Enter 换行" : "打开蓝牙后即可继续发送"), "localized hint matches active configuration");
            page.Measure(new Size(1000, 760)); page.Arrange(new Rect(0, 0, 1000, 760)); page.UpdateLayout();
            var card = (FrameworkElement)page.FindName("ComposerSettingsCard");
            check(choice.ActualWidth >= 180 && choice.ActualHeight >= 30 && card.ActualWidth > choice.ActualWidth,
                "shortcut setting fits the standard settings card");
            var bitmap = new RenderTargetBitmap(1000, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, $"composer-shortcuts-{language}-{theme}.png")); png.Save(output);
        }
        wait(window.ViewModel.SaveSettingsAsync(original));
    }

    private sealed class OffscreenSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
