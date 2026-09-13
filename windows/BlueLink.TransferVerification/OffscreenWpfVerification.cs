using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private int _checks;
    private readonly List<object> _images = [];
    private readonly List<string> _passedChecks = [];
    private readonly List<string> _geometryFailures = [];

    public void Run(string outputDirectory, bool fileAvailabilityOnly = false, bool imagePreviewOnly = false, bool interactionsOnly = false, bool usbOnly = false, bool identityOnly = false, bool workspaceOnly = false, bool settingsOnly = false, bool nearbyOnly = false)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlueLinkOffscreen-" + Guid.NewGuid().ToString("N")));
            Application? app = null;
            MainViewModel? model = null;
            SettingsPage? settings = null;
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; };
                if (nearbyOnly)
                {
                    VerifyControlInteractions(dataRoot, output);
                    VerifyStartupLanguageAndComposer(dataRoot, output);
                    VerifyMainWindowSizing(dataRoot, output);
                    VerifyDisabledHomeActions(dataRoot, output);
                    VerifyHomeStateCoverage(dataRoot, output);
                    VerifyNearbyHeader(dataRoot, output);
                    return;
                }
                if (identityOnly)
                {
                    VerifyIdentityAssociationDesign(output);
                    return;
                }
                if (usbOnly)
                {
                    VerifyUsbSessionDesign(dataRoot, output);
                    return;
                }
                if (interactionsOnly)
                {
                    VerifyControlInteractions(dataRoot, output);
                    VerifyImagePreviewDesign(dataRoot, output);
                    return;
                }
                if (imagePreviewOnly)
                {
                    VerifyImagePreviewDesign(dataRoot, output);
                    return;
                }
                if (fileAvailabilityOnly)
                {
                    VerifyFileAvailabilityDialogs(dataRoot, output);
                    return;
                }
                if (!settingsOnly)
                {
                    if (!workspaceOnly)
                    {
                        VerifyControlInteractions(dataRoot, output);
                        VerifyStartupLanguageAndComposer(dataRoot, output);
                        VerifyMainWindowSizing(dataRoot, output);
                        VerifyImagePreviewDesign(dataRoot, output);
                        VerifyDisabledHomeActions(dataRoot, output);
                        VerifyHomeStateCoverage(dataRoot, output);
                        VerifyNearbyHeader(dataRoot, output);
                        VerifyConversationIndicators(dataRoot, output);
                        VerifyMessageScenes(dataRoot, output);
                        VerifyFileDropScenes(dataRoot, output);
                    }
                    VerifyFileWorkspaceScope(dataRoot, output);
                    VerifyMessageSearchScenes(dataRoot, output);
                    VerifyFileContextMenus(dataRoot, output);
                    VerifyDeviceTransferStates(dataRoot, output);
                    VerifyInformationDialogs(dataRoot, output);
                    VerifyConfirmationDialogs(output);
                    VerifyFileAvailabilityDialogs(dataRoot, output);
                    VerifyTransientToasts(dataRoot, output);
                }
                if (workspaceOnly) return;
                model = new MainViewModel(dataRoot);
                WaitForUiTask(model.InitializeLocalStateAsync());
                WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "light" }));
                VerifySettingsDialogs(model, output);
                settings = new SettingsPage(model);
                Check(PresentationSource.FromVisual(settings) is null, "no desktop window created");
                var content = DetachForRendering(settings);
                foreach (var (name, control) in new[]
                {
                    ("general", "GeneralNavigationItem"), ("connection-empty", "ConnectionNavigationItem"),
                    ("files", "FilesNavigationItem"), ("privacy", "PrivacyNavigationItem"), ("about", "AboutNavigationItem"),
                })
                {
                    ((FrameworkElement)settings.FindName(control)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Capture(content, output, name, 1600, 1000);
                    Check(((FrameworkElement)settings.FindName("SettingsFooter")).Visibility == (name == "about" ? Visibility.Collapsed : Visibility.Visible),
                        $"only editable settings reserve footer space: {name}");
                    Check(((FrameworkElement)settings.FindName("FooterActions")).Visibility ==
                          (name == "about" ? Visibility.Collapsed : Visibility.Visible),
                        $"only editable settings show save actions: {name}");
                }
                Capture(content, output, "about-minimum", 1000, 600);
                Check(((Border)settings.FindName("AboutBrandCard")).ActualHeight <= 166,
                    "about brand card stays compact at minimum width");

                Click(content, "帮助与反馈");
                Capture(content, output, "feedback", 1600, 1000);
                Check(((FrameworkElement)settings.FindName("SettingsFooter")).Visibility == Visibility.Collapsed,
                    "feedback has no settings-save footer");
                var description = (Wpf.Ui.Controls.TextBox)settings.FindName("FeedbackDescription");
                description.Text = "QA 后台布局验收";
                Check(((TextBlock)settings.FindName("FeedbackCharacterCount")).Text == $"{description.Text.Length} / 1000",
                    "description counter responds without physical input");
                VerifyFeedbackStates(settings, content, output);
                description.Text = "QA 后台布局验收";
                var topic = Descendants<Button>(content).First(value => (string?)value.Tag == "connection");
                topic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Capture(content, output, "help-connection", 1600, 1000);
                Check(Descendants<Expander>(content).Count(value => value.IsExpanded) == 1,
                    "help topic opens the first answer");

                GoAbout(settings);
                Click(content, "隐私政策");
                Capture(content, output, "privacy-document", 1600, 1000);
                Check(((StackPanel)settings.FindName("DocumentTopics")).Children.OfType<Button>().Count() == 4,
                    "privacy document has four directory entries");
                Check(((FrameworkElement)settings.FindName("SettingsFooter")).Visibility == Visibility.Collapsed &&
                      ((FrameworkElement)settings.FindName("FooterActions")).Visibility == Visibility.Collapsed,
                    "legal pages use available height without an empty footer");
                Capture(content, output, "privacy-document-minimum", 1000, 600);
                var documentEntries = ((StackPanel)settings.FindName("DocumentTopics")).Children.OfType<Button>().ToArray();
                documentEntries[^1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Layout(content, 1000, 600);
                Check(((SolidColorBrush)documentEntries[^1].Background).Color ==
                      ((SolidColorBrush)settings.FindResource("SettingsSoftBlueBrush")).Color &&
                      documentEntries[0].Background == Brushes.Transparent,
                    "document directory changes the selected section");
                var documentScroll = (ScrollViewer)settings.FindName("HelpDetailScroll");
                var lastDocumentSection = (Border)((StackPanel)settings.FindName("HelpDetail")).Children[^1];
                var sectionBody = (Grid)lastDocumentSection.Child;
                Check(sectionBody.TranslatePoint(new Point(0, sectionBody.ActualHeight), documentScroll).Y <=
                      documentScroll.ActualHeight - documentScroll.Padding.Bottom + 1,
                    "directory navigation reveals the selected section body, not only its heading");
                GoAbout(settings);
                Click(content, "用户协议");
                Capture(content, output, "agreement", 1600, 1000);
                GoAbout(settings);
                Click(content, "开源许可");
                Capture(content, output, "licenses", 1600, 1000);
                Check(((FrameworkElement)settings.FindName("HelpNavigationCard")).Visibility == Visibility.Collapsed &&
                      ((FrameworkElement)settings.FindName("LicenseNavigation")).Visibility == Visibility.Visible,
                    "license search and list replace the document directory");
                Capture(content, output, "licenses-minimum", 1000, 600);
                var licenseScroll = (ScrollViewer)settings.FindName("HelpDetailScroll");
                Check(Descendants<TextBlock>(licenseScroll).Any(text => text.Text == BlueLink.Legal.LicenseCatalog.All[0].Text) &&
                      licenseScroll.ScrollableHeight > 0,
                    "minimum license view preserves the entire original license in a scrollable panel");
                licenseScroll.ScrollToEnd();
                Layout(content, 1000, 600);
                Check(Math.Abs(licenseScroll.VerticalOffset - licenseScroll.ScrollableHeight) < 1,
                    "original license can be read to its final line");
                ((Wpf.Ui.Controls.TextBox)settings.FindName("LicenseQuery")).Text = "BouncyCastle";
                Check(((StackPanel)settings.FindName("LicenseEntries")).Children.OfType<Button>().Count() == 1,
                    "license query filters the real component list");
                ((Wpf.Ui.Controls.TextBox)settings.FindName("LicenseQuery")).Text = "no-such-QA-license";
                Check(((StackPanel)settings.FindName("LicenseEntries")).Children.OfType<Button>().Any() == false,
                    "unknown license query displays an empty state");
                GoAbout(settings);
                Click(content, "帮助与反馈");
                Check(description.Text == "QA 后台布局验收", "feedback draft survives document navigation");
                Capture(content, output, "feedback-compact", 1000, 600);
                var scroll = (ScrollViewer)settings.FindName("HelpDetailScroll");
                Check(scroll.ScrollableHeight > 0, "compact feedback can scroll vertically");
                scroll.ScrollToEnd();
                Layout(content, 1000, 600);
                Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                    "compact feedback can reach its bottom");
                Capture(content, output, "feedback-compact-bottom", 1000, 600);
                Check(PresentationSource.FromVisual(settings) is null,
                    "verification never creates or activates a native settings window");

                ((FrameworkElement)settings.FindName("GeneralNavigationItem")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((ComboBox)settings.FindName("ThemeBox")).SelectedValue = "dark";
                ((ComboBox)settings.FindName("LanguageBox")).SelectedValue = "en-US";
                Check(BlueLink.Localization.Strings.Language == "zh-CN" && BlueLink.Appearance.AppearanceService.CurrentTheme == Wpf.Ui.Appearance.ApplicationTheme.Light,
                    "appearance draft has no effect before saving");
                WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "dark", Language = "en-US" }));
                Capture(content, output, "general-dark-english-live", 1600, 1000);
                Check(((Wpf.Ui.Controls.NavigationViewItem)settings.FindName("GeneralNavigationItem")).Content as string == "General",
                    "existing XAML text updates when language changes");
                Check(((SolidColorBrush)app.FindResource("SettingsCanvasBrush")).Color == Color.FromRgb(0x12, 0x17, 0x22),
                    "dark palette applied to application resources");
                settings.Dispose();
                settings = new SettingsPage(model);
                content = DetachForRendering(settings);
                Check((string?)((ComboBox)settings.FindName("ThemeBox")).SelectedValue == "dark" &&
                      (string?)((ComboBox)settings.FindName("LanguageBox")).SelectedValue == "en-US",
                    "reopened settings restore saved theme and language");
                Capture(content, output, "general-dark-english-compact", 1040, 680);
                ((FrameworkElement)settings.FindName("RestoreDefaultsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(BlueLink.Localization.Strings.Language == "en-US" && model.Settings.Theme == "dark",
                    "restoring defaults only changes the draft");
                GoAbout(settings);
                Click(content, "Help & feedback");
                Capture(content, output, "feedback-dark-english", 1600, 1000);
                GoAbout(settings);
                Click(content, "Privacy policy");
                Capture(content, output, "privacy-dark-english", 1040, 680);
                Check(((TextBlock)settings.FindName("HelpPageTitle")).Text == "Privacy policy",
                    "generated document headings use saved language");
                WaitForUiTask(model.InitializeLocalStateAsync());
                Check(model.Settings.Theme == "dark" && model.Settings.Language == "en-US",
                    "local initialization restores persisted appearance without starting Bluetooth");
                var reset = new ResetIdentityWindow();
                Capture(DetachForRendering(reset), output, "reset-identity-dark-english", 540, 320);
                Check(new WindowInteropHelper(reset).Handle == IntPtr.Zero, "identity reset dialog is rendered without a native window");
                reset.Close();
                var fingerprintBeforeReset = model.IdentityFingerprint;
                WaitForUiTask(model.ResetIdentityAsync());
                Check(model.IdentityFingerprint != fingerprintBeforeReset && model.Settings.Language == "en-US" && !model.HasTrustedDevices,
                    "real identity reset updates the model and preserves settings without starting Bluetooth");
                WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "light", Language = "zh-CN" }));
                Check(BlueLink.Localization.Strings.Get("通用") == "通用" &&
                      ((SolidColorBrush)app.FindResource("SettingsCanvasBrush")).Color == Color.FromRgb(0xF6, 0xF8, 0xFB),
                    "switching back restores exact Figma light palette and Chinese");
                Check(PresentationSource.FromVisual(settings) is null,
                    "theme and language verification creates no desktop window");

                settings.Dispose();
                settings = new SettingsPage(model);
                content = DetachForRendering(settings);
                VerifyUsbPages(model, settings, content, output);
                var notifications = new BlueLink.Notifications.NotificationCenter();
                notifications.Receive("qa-phone", "QA Pixel 10", Guid.NewGuid(), "QA 新消息预览", false);
                notifications.Receive("qa-phone", "QA Pixel 10", Guid.NewGuid(), "QA 第二条新消息", false);
                notifications.Receive("qa-pc", "QA SURFACE-LAPTOP", Guid.NewGuid(), "文件接收完成", false);
                var trayView = new TrayConversationView(notifications);
                var dismissed = false; trayView.DismissRequested += () => dismissed = true;
                var opened = ""; trayView.ConversationRequested += peer => opened = peer;
                Capture(trayView, output, "tray-conversation-preview", 400, 274);
                Check(Descendants<System.Windows.Controls.ScrollViewer>(trayView).First().ScrollableHeight < 1,
                    "two tray conversations fit the prototype without a vertical scrollbar");
                Check(trayView.TryFindResource("PanelF8FAFDBrush") is SolidColorBrush,
                    "tray conversation row background resolves in the application palette");
                Click(trayView, "暂不处理");
                Check(dismissed && notifications.UnreadCount == 3, "dismissing tray preview preserves unread messages");
                Descendants<Button>(trayView).First(value => value.DataContext is BlueLink.Notifications.NotificationConversation).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(opened is "qa-phone" or "qa-pc", "tray conversation action selects the real peer identity");
                VerifyUpdateWindows(model, dataRoot, output);
                var localIdentity = BlueLink.Security.DeviceIdentity.Generate();
                var remoteIdentity = BlueLink.Security.DeviceIdentity.Generate();
                foreach (var stage in new[] { BlueLink.Security.TrustStage.Confirm, BlueLink.Security.TrustStage.Waiting,
                    BlueLink.Security.TrustStage.Rejected, BlueLink.Security.TrustStage.TimedOut, BlueLink.Security.TrustStage.IdentityChanged })
                {
                    var request = new BlueLink.Security.TrustRequest("QA Pixel 10", "482 916",
                        BlueLink.Security.TrustRequest.Fingerprint(localIdentity.PublicKey),
                        BlueLink.Security.TrustRequest.Fingerprint(remoteIdentity.PublicKey), trustedFingerprint:
                        BlueLink.Security.TrustRequest.Fingerprint(BlueLink.Security.DeviceIdentity.Generate().PublicKey));
                    var trust = new TrustConfirmationWindow(request);
                    var trustContent = DetachForRendering(trust);
                    if (stage == BlueLink.Security.TrustStage.Waiting)
                        ((FrameworkElement)trust.FindName("TrustPrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    else if (stage != BlueLink.Security.TrustStage.Confirm) request.Finish(stage);
                    Capture(trustContent, output, "security-" + stage.ToString().ToLowerInvariant(), 620, 576);
                    Check(request.Stage == stage && !trust.IsVisible && new WindowInteropHelper(trust).Handle == IntPtr.Zero,
                        "security state binds without a native window: " + stage);
                    var trustButtons = Descendants<Button>(trustContent).ToArray();
                    Check(trustButtons.Length == 3 && trustButtons.All(button =>
                    {
                        var bounds = button.TransformToAncestor(trustContent).TransformBounds(new Rect(button.RenderSize));
                        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 620 && bounds.Bottom <= 576;
                    }), "all security actions are visible within the 620 by 576 dialog: " + stage);
                    Check(((Button)trust.FindName("TrustPrimaryButton")).ActualWidth >= 270 && ((Button)trust.FindName("TrustCloseButton")).ActualWidth >= 270,
                        "security footer buttons fill both equal-width columns: " + stage);
                    var prototypeScroll = (ScrollViewer)trust.FindName("TrustContentScroll");
                    Check(prototypeScroll.ScrollableHeight < 1, $"security body fits without unnecessary scrolling: {stage}, extent={prototypeScroll.ExtentHeight}, viewport={prototypeScroll.ViewportHeight}, actual={prototypeScroll.ActualHeight}");
                    var stateText = (TextBlock)trust.FindName("TrustStatusText");
                    Check(stateText.ActualHeight == (stage == BlueLink.Security.TrustStage.Confirm ? 60 : 32) &&
                        (stage is BlueLink.Security.TrustStage.Confirm or BlueLink.Security.TrustStage.Waiting || !stateText.Text.Contains(request.SafetyCode)),
                        "security states use the prototype line height and terminal states hide the expired code: " + stage);
                    Check(stateText.Foreground is SolidColorBrush stateBrush && stateBrush.Color ==
                        ((SolidColorBrush)Application.Current.FindResource(stage is BlueLink.Security.TrustStage.TimedOut or BlueLink.Security.TrustStage.IdentityChanged ? "DangerBrush" : stage == BlueLink.Security.TrustStage.Rejected ? "MutedBrush" : "BlueBrush")).Color,
                        "security status tone follows the current request state: " + stage);
                    if (stage == BlueLink.Security.TrustStage.IdentityChanged)
                        Check(((TextBlock)trust.FindName("SecondFingerprintLabel")).Foreground is SolidColorBrush fingerprintBrush &&
                            fingerprintBrush.Color == ((SolidColorBrush)Application.Current.FindResource("DangerBrush")).Color &&
                            request.FirstFingerprint == request.TrustedFingerprint,
                            "identity change highlights the current fingerprint and compares against the pinned identity");
                    if (stage == BlueLink.Security.TrustStage.Confirm)
                    {
                        Capture(trustContent, output, "security-confirm-low-height", 620, 480);
                        var compactTrustScroll = (ScrollViewer)trust.FindName("TrustContentScroll");
                        Check(compactTrustScroll.ScrollableHeight > 0 && ((Button)trust.FindName("TrustPrimaryButton")).TransformToAncestor(trustContent).TransformBounds(new Rect(((Button)trust.FindName("TrustPrimaryButton")).RenderSize)).Bottom <= 480,
                            "security content can scroll while its footer remains reachable at a low available height");
                    }
                    if (stage == BlueLink.Security.TrustStage.Waiting)
                    {
                        var waitingButton = (Button)trust.FindName("TrustPrimaryButton");
                        Check(!waitingButton.IsEnabled && waitingButton.Opacity == .5, "waiting state prevents duplicate confirmation");
                        Check(Descendants<Border>(waitingButton).Any(border => border.Background is SolidColorBrush brush && brush.Color ==
                            ((SolidColorBrush)Application.Current.FindResource("BlueBrush")).Color),
                            "waiting button retains a blue background so its disabled white label remains visible");
                    }
                    if (stage == BlueLink.Security.TrustStage.IdentityChanged)
                    {
                        WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "dark", Language = "en-US" }));
                        // Create after language switch: request state strings are calculated in the current language.
                        trust.Close();
                        trust = new TrustConfirmationWindow(request);
                        trustContent = DetachForRendering(trust);
                        Capture(trustContent, output, "security-identity-changed-dark-english", 620, 576);
                    }
                    trust.Close();
                }

            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try
                {
                    settings?.Dispose();
                    DrainDispatcher();
                    model?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    app?.Shutdown();
                    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (dataRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(dataRoot).StartsWith("BlueLinkOffscreen-", StringComparison.Ordinal) && Directory.Exists(dataRoot))
                        Directory.Delete(dataRoot, recursive: true);
                }
                catch (Exception exception) { failure ??= exception; }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Software rendering and fixture I/O share the user's PC; keep a bounded allowance for background load.
        if (!thread.Join(TimeSpan.FromMinutes(10))) throw new TimeoutException("Offscreen WPF verification timed out.");
        if (failure is not null) throw new InvalidOperationException("Offscreen WPF verification failed.", failure);
        File.WriteAllText(Path.Combine(output, "geometry-failures.json"), JsonSerializer.Serialize(_geometryFailures));
        Check(_geometryFailures.Count == 0, "Content geometry failures: " + string.Join("; ", _geometryFailures));
        File.WriteAllText(Path.Combine(output, "layout-results.json"), JsonSerializer.Serialize(new
        {
            mode = "offscreen-wpf-content", nativeSettingsWindowCreated = false, physicalInputSent = false,
            rendering = "software", checks = _checks, images = _images, passedChecks = _passedChecks,
            limitations = "Content rendering and routed-event checks; not desktop UIA, native dialogs, physical input or actual monitor DPI acceptance.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"BlueLink offscreen WPF verification passed: {_checks} checks; {_images.Count} images; native settings HWND = 0");
    }

    private void VerifyStartupLanguageAndComposer(string dataRoot, string output)
    {
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            var directory = Path.Combine(dataRoot, "startup-language-" + language);
            var seed = new MainViewModel(directory);
            try
            {
                WaitForUiTask(seed.InitializeLocalStateAsync());
                WaitForUiTask(seed.SaveSettingsAsync(seed.Settings with
                { Language = language, Theme = language == "zh-CN" ? "light" : "dark", ScanOnStartup = false }));
            }
            finally { WaitForUiTask(seed.DisposeAsync().AsTask()); }
            var previousLanguage = language == "zh-CN" ? "en-US" : "zh-CN";
            BlueLink.Localization.Strings.Apply(previousLanguage);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                var root = DetachForRendering(window);
                var model = window.ViewModel;
                var sidebar = (Border)window.FindName("DevicesSidebar");
                sidebar.ContextMenu.PlacementTarget = sidebar;
                var scan = (MenuItem)sidebar.ContextMenu.Items[0];
                var input = (Wpf.Ui.Controls.TextBox)window.FindName("MessageInput");
                Layout(root, 1000, 600);
                Check((string)scan.Header == (previousLanguage == "zh-CN" ? "扫描附近设备" : "Scan nearby devices"),
                    "scan binding is evaluated before loading persisted language: " + language);
                WaitForUiTask(model.InitializeLocalStateAsync());
                window.LoadVisualFixture();
                Layout(root, 1000, 600);
                Check(BlueLink.Localization.Strings.Language == language &&
                    (string)scan.Header == (language == "zh-CN" ? "扫描附近设备" : "Scan nearby devices") &&
                    System.Windows.Automation.AutomationProperties.GetName(scan) == model.RefreshNearbyText,
                    "persisted language refreshes existing scan caption and automation name: " + language);
                Capture(root, output, "startup-language-" + language, 1000, 600);
                foreach (var selectedLanguage in new[] { previousLanguage, language })
                {
                    WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Language = selectedLanguage }));
                    Layout(root, 1000, 600);
                    Check((string)scan.Header == (selectedLanguage == "zh-CN" ? "扫描附近设备" : "Scan nearby devices") &&
                        input.PlaceholderText == model.ComposerPlaceholder,
                        "live language switch refreshes scan and composer together: " + language + " to " + selectedLanguage);
                }
                // Set only the WPF trigger state; never request keyboard focus or create an HWND.
                var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsFocusedPropertyKey",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
                input.Text = language == "zh-CN" ? "第一行消息\n第二行消息" : "First message line\nSecond message line";
                try
                {
                    input.SetValue(focusKey, true);
                    Capture(root, output, "composer-focused-" + language, 1000, 600);
                    Check(input.IsFocused && input.Text.Contains('\n') && input.AcceptsReturn && input.TextWrapping == TextWrapping.Wrap,
                        "composer retains multiline editing in the simulated focus state: " + language);
                }
                finally { input.SetValue(focusKey, false); }
                input.IsEnabled = false;
                input.Clear();
                Capture(root, output, "composer-disabled-" + language, 1000, 600);
                var frame = (Border)((Border)window.FindName("MessageComposer")).Child;
                Check(frame.BorderThickness == new Thickness(1) && frame.CornerRadius == new CornerRadius(14),
                    "removing the input underline retains the outer composer frame: " + language);
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
    }

    private void VerifyDisabledHomeActions(string dataRoot, string output)
    {
        foreach (var scene in new[] { "bluetooth-off", "zero-connected", "connect-loading" })
        {
            var directory = Path.Combine(dataRoot, "disabled-actions-" + scene);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, scene));
                var root = DetachForRendering(window);
                Capture(root, output, "home-" + scene, 1180, 720);
                var label = scene == "connect-loading" ? "连接中" : scene == "zero-connected" ? "离线" : "发送";
                var button = Descendants<Button>(root).Single(value => value.Visibility == Visibility.Visible &&
                    System.Windows.Automation.AutomationProperties.GetName(value) == label);
                Check(!button.IsEnabled, scene + " action is semantically disabled");
                var border = (Border)button.Template.FindName("ContentBorder", button);
                var text = Descendants<TextBlock>(button).Single(value => value.Text == label);
                var background = ((SolidColorBrush)border.Background).Color;
                var foreground = ((SolidColorBrush)text.Foreground).Color;
                Check(Math.Abs(background.R - foreground.R) + Math.Abs(background.G - foreground.G) +
                    Math.Abs(background.B - foreground.B) > 150, scene + " disabled action keeps readable text");
                if (scene == "bluetooth-off")
                    Check(window.ViewModel.ComposerPlaceholder.Contains("蓝牙未开启", StringComparison.Ordinal) &&
                        window.ViewModel.ComposerHint.Contains("打开蓝牙", StringComparison.Ordinal) && !window.ViewModel.ShowOfflineHistoryNotice,
                        "Bluetooth-off guidance is distinct from offline history and reacts without a system toggle");
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, scene + " regression creates no native window");
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
    }

    private void VerifyHomeStateCoverage(string dataRoot, string output)
    {
        foreach (var scene in new[] { "first-use", "connected-empty", "offline-empty", "nearby-empty", "connect-failed", "connect-success", "message-history", "files-offline", "files-bluetooth-off", "settings-trusted" })
        {
            var directory = Path.Combine(dataRoot, "reverse-audit-" + scene);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, scene));
                var root = DetachForRendering(window);
                Capture(root, output, "audit-" + scene, 1180, 720);
                if (scene == "first-use") Check(window.ViewModel.Conversations.Count == 0, "first-use has no historical conversations");
                if (scene == "connected-empty") Check(!window.ViewModel.Conversations.Any(item => item.IsConnected), "empty connected category reflects the live collection");
                if (scene == "offline-empty") Check(!window.ViewModel.Conversations.Any(item => item.IsOffline), "empty offline category reflects the live collection");
                if (scene == "nearby-empty") Check(window.ViewModel.Devices.Count == 0, "empty nearby category reflects the live collection");
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "reverse-audit state never creates an HWND: " + scene);
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
        var stateDirectory = Path.Combine(dataRoot, "reverse-audit-control-states");
        var main = new MainWindow(initializeRuntime: false, dataRoot: stateDirectory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, stateDirectory, "connected"));
            var root = DetachForRendering(main);
            Capture(root, output, "audit-home-expanded", 1180, 720);
            foreach (var category in new[] { "Connected", "Offline", "Nearby" })
            {
                var toggle = Descendants<Button>(root).First(button => button.Tag as string == category);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Capture(root, output, "audit-category-collapsed-" + category.ToLowerInvariant(), 1180, 720);
                var property = typeof(MainViewModel).GetProperty(category + "Expanded")!;
                Check(!(bool)property.GetValue(main.ViewModel)!, "actual category handler collapses " + category);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check((bool)property.GetValue(main.ViewModel)!, "actual category handler restores " + category);
            }
            Check(main.FindName("ScanButton") is null, "the permanent scan button is removed");
            var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var pressedKey = (DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            foreach (var name in new[] { "NearbyConnect" })
            {
                var button = Descendants<Button>(root).First(value => value.Name == name && value.Visibility == Visibility.Visible && value.ActualWidth > 0);
                var originalContent = button.Content;
                button.SetValue(hoverKey, true);
                button.SetValue(pressedKey, true);
                Capture(root, output, "audit-pressed-" + name, 1180, 720);
                Check(button.IsPressed && button.IsEnabled && ReferenceEquals(button.Content, originalContent), "pressed state preserves enabled action and caption: " + name);
                var contentBorder = (Border)button.Template.FindName("ContentBorder", button);
                Check(((SolidColorBrush)contentBorder.Background).Color.ToString() == "#FFEBF2FF", "pressed button renders the prototype fill: " + name);
                Check(((SolidColorBrush)contentBorder.BorderBrush).Color.ToString() == "#FF0A4FD1", "pressed button renders the prototype border: " + name);
                button.SetValue(pressedKey, false);
                button.SetValue(hoverKey, false);
                Check(!button.IsPressed, "pressed state restores without firing a command: " + name);
            }
            Check(new WindowInteropHelper(main).Handle == IntPtr.Zero, "routed category checks and simulated press states send no physical input");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyNearbyHeader(string dataRoot, string output)
    {
        foreach (var scene in new[] { "refresh-complete", "refresh-scanning" })
        {
            var directory = Path.Combine(dataRoot, "nearby-header-" + scene);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, scene));
                var root = DetachForRendering(window);
                var model = window.ViewModel;
                var scan = (StackPanel)window.FindName("NearbyScanStatus");
                var sidebar = (Border)window.FindName("DevicesSidebar");
                sidebar.ContextMenu.PlacementTarget = sidebar;
                var refresh = (MenuItem)sidebar.ContextMenu.Items[0];
                var title = (Button)window.FindName("NearbyGroupTitleButton");
                var toggle = (Button)window.FindName("NearbyGroupToggle");
                var chevron = (Image)window.FindName("NearbyGroupChevron");
                var content = (StackPanel)window.FindName("NearbyGroupContent");
                var scroll = (ScrollViewer)window.FindName("DeviceGroupsScroll");
                foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
                {
                    WaitForUiTask(model.SaveSettingsAsync(model.Settings with
                    { Language = language, Theme = language == "en-US" ? "dark" : "light" }));
                    foreach (var (width, height) in new[] { (1000, 600), (1180, 720), (1600, 1000) })
                    {
                        Layout(root, width, height);
                        scroll.ScrollToEnd();
                        var name = $"nearby-header-{scene}-{language}-{width}";
                        if (width == 1000) Capture(root, output, name, width, height);
                        else
                        {
                            Layout(root, width, height);
                            VerifyContentGeometry(root, name);
                        }
                        VerifyDeviceGroupSpacing(window, name);
                        Check(window.FindName("ScanButton") is null && refresh.IsEnabled == model.CanScan && refresh.InputGestureText == "F5" && sidebar.ContextMenu.MinWidth == 190,
                            "refresh menu replaces the permanent scan button and follows scan availability: " + name);
                        Check(scan.Visibility == (model.IsScanning ? Visibility.Visible : Visibility.Collapsed),
                            "search progress appears only during scanning: " + name);
                        if (model.IsScanning)
                        {
                            var caption = Descendants<TextBlock>(scan).Single();
                            var text = new FormattedText(caption.Text, System.Globalization.CultureInfo.GetCultureInfo(language),
                                caption.FlowDirection, new Typeface(caption.FontFamily, caption.FontStyle, caption.FontWeight, caption.FontStretch),
                                caption.FontSize, caption.Foreground, VisualTreeHelper.GetDpi(caption).PixelsPerDip);
                            Check(text.WidthIncludingTrailingWhitespace <= caption.ActualWidth + 1,
                                "search status caption fits at the minimum sidebar width: " + name);
                        }
                    }
                    title.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (scene == "refresh-complete" && language == "zh-CN")
                        Capture(root, output, "nearby-header-collapsed-minimum", 1000, 600);
                    else
                    {
                        Layout(root, 1000, 600);
                        VerifyContentGeometry(root, $"nearby-header-{scene}-{language}-collapsed");
                    }
                    Check(!model.NearbyExpanded && content.Visibility == Visibility.Collapsed &&
                        chevron.RenderTransform is RotateTransform { Angle: -90 },
                        "nearby title collapses its content and rotates the chevron: " + scene + "/" + language);
                    toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Layout(root, 1000, 600);
                    Check(model.NearbyExpanded && content.Visibility == Visibility.Visible && chevron.RenderTransform.Value.IsIdentity &&
                        model.IsScanning == (scene == "refresh-scanning"),
                        "nearby chevron restores its content without changing scan state: " + scene + "/" + language);
                }
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
    }

    private void VerifyDeviceGroupSpacing(MainWindow window, string scene)
    {
        foreach (var name in new[] { "Connected", "Offline", "Nearby" })
        {
            var group = (StackPanel)window.FindName(name + "DeviceGroup");
            var list = (ListBox)window.FindName(name + "DeviceList");
            var header = (FrameworkElement)group.Children[0];
            var cards = Descendants<Border>(list).Where(card => card.Name is "ConversationCard" or "NearbyCard").ToArray();
            if (cards.Length == 0) continue;
            Check(cards.All(card => Math.Abs(card.ActualHeight - 60) < 0.1),
                $"idle device cards share sixty DIP height: {name}/{scene}");
            var first = cards[0].TranslatePoint(new Point(), group).Y;
            var last = cards[^1].TranslatePoint(new Point(0, cards[^1].ActualHeight), group).Y;
            var topGap = first - header.ActualHeight;
            var bottomGap = group.ActualHeight + group.Margin.Bottom - last;
            Check(Math.Abs(topGap - bottomGap) < 0.1,
                $"device group has equal top/bottom spacing: {name}/{scene}, {topGap}/{bottomGap}");
            for (var index = 1; index < cards.Length; index++)
            {
                var gap = cards[index].TranslatePoint(new Point(), list).Y -
                    cards[index - 1].TranslatePoint(new Point(0, cards[index - 1].ActualHeight), list).Y;
                Check(Math.Abs(gap - 10) < 0.1, $"device cards retain ten DIP separation (eight plus container insets): {name}/{scene}, {gap}");
            }
        }
    }

    private void VerifyConversationIndicators(string dataRoot, string output)
    {
        var window = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(dataRoot, "conversation-indicators"));
        try
        {
            var model = window.ViewModel;
            WaitForUiTask(model.InitializeLocalStateAsync());
            window.LoadVisualFixture();
            var connected = model.ConnectedConversations.Single();
            var offline = model.OfflineConversations.Single();
            var root = DetachForRendering(window);
            foreach (var (count, state) in new[] { (0, "read"), (7, "unread"), (100, "overflow"), (0, "cleared") })
            {
                model.ConnectedConversations[0] = connected with { UnreadCount = count };
                model.OfflineConversations[0] = offline with { UnreadCount = count };
                foreach (var (width, height) in new[] { (1000, 600), (1180, 720) })
                {
                    var scene = $"conversation-indicators-{state}-{width}";
                    if (width == 1000 && state != "cleared") Capture(root, output, scene, width, height);
                    else
                    {
                        Layout(root, width, height);
                        VerifyContentGeometry(root, scene);
                    }
                    var cards = Descendants<Border>(root).Where(value => value.Name == "ConversationCard" && Displayed(value, root)).ToArray();
                    Check(cards.Length == 2 && cards.All(card => card.DataContext is BlueLink.Domain.ConversationSummary { } peer &&
                        peer.UnreadCount == count), "both connected and offline cards refresh unread counts: " + scene);
                }
            }
        }
        finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
    }

    private void VerifyMessageScenes(string dataRoot, string output)
    {
        foreach (var scene in new[] { "message-empty", "message-statuses" })
        {
            var directory = Path.Combine(dataRoot, scene);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, scene));
                var root = DetachForRendering(window);
                Capture(root, output, scene + "-minimum", 1000, 600);
                if (scene == "message-empty")
                    Check(window.ViewModel.IsConversationEmpty && Descendants<TextBlock>(root).Any(value =>
                        value.Text == window.ViewModel.EmptyConversationHint && value.ActualWidth > 0),
                        "empty conversation displays guidance for the selected peer");
                else
                {
                    // Realize all five rows before checking content; the minimum viewport intentionally scrolls.
                    Capture(root, output, scene + "-all", 1600, 1000);
                    var statuses = Descendants<TextBlock>(root).Where(value => value.Name == "MessageMeta").ToArray();
                    static string StatusLabel(TextBlock value) => string.Concat(value.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
                    Check(statuses.Length == 5 && window.ViewModel.Messages.All(item => statuses.Any(value => StatusLabel(value) == item.StatusText)),
                        "all five outgoing states bind their visible status labels");
                    Check(Descendants<Image>(root).Count(value => value.Name == "MessageStatusIcon" && value.Source is not null && value.Visibility == Visibility.Visible) == 5,
                        "all five outgoing states retain their exact source icon");
                    var blue = ((SolidColorBrush)root.FindResource("BlueBrush")).Color;
                    var red = ((SolidColorBrush)root.FindResource("DangerBrush")).Color;
                    Check(statuses.Where(value => StatusLabel(value) is "已读" or "发送中")
                        .All(value => ((SolidColorBrush)value.Foreground).Color == blue) &&
                        statuses.Single(value => StatusLabel(value) == "发送失败").Foreground is SolidColorBrush failed && failed.Color == red,
                        "sending and read are blue while failed remains red");
                }
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, scene + " verification never creates a native window");
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
    }

    private void VerifyFileDropScenes(string dataRoot, string output)
    {
        foreach (var scene in new[] { "drop-send", "drop-blocked" })
        {
            var directory = Path.Combine(dataRoot, scene);
            var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, scene));
                var root = DetachForRendering(window);
                Capture(root, output, scene + "-minimum", 1000, 600);
                var canSend = scene == "drop-send";
                Check(window.ShowFileDropFeedback(1) == canSend, scene + " reports the actual connection eligibility");
                var outline = (System.Windows.Shapes.Rectangle)window.FindName("FileDropOutline");
                var expected = ((SolidColorBrush)root.FindResource(canSend ? "SoftBlueBrush" : "SoftWarningBrush")).Color;
                Check(outline.StrokeDashArray.Count > 0 && ((SolidColorBrush)outline.Fill).Color == expected,
                    scene + " has its distinct opaque fill and dashed outline");
                Check(((TextBlock)window.FindName("FileDropTitle")).Text == (canSend ? "释放以发送文件" : "设备未连接，无法发送文件"),
                    scene + " displays the matching prototype instruction");
                Check(!window.ShowFileDropFeedback(0), "empty or non-file payload is blocked even when connected");
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, scene + " rendering creates no native window");
            }
            finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
        }
    }

    private void VerifyFileWorkspaceScope(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "file-workspace-scope");
        var window = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(window, directory, "files-current"));
            var root = DetachForRendering(window);
            Capture(root, output, "files-current-minimum", 1000, 600);
            var host = (Border)window.FindName("FileSearchHost");
            var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            host.SetValue(hoverKey, true);
            Capture(root, output, "audit-file-search-hover", 1000, 600);
            Check(host.BorderThickness == new Thickness(1.5) && ((SolidColorBrush)host.BorderBrush).Color.ToString() == "#FF1677FF", "file search hover uses the prototype 1.5 DIP blue border");
            var searchInput = (Wpf.Ui.Controls.TextBox)window.FindName("FileSearchInput");
            Check(searchInput.FontSize == 13 && searchInput.ActualHeight <= host.ActualHeight - 3, "file search text remains within its hover border");
            host.SetValue(hoverKey, false);
            Check(host.BorderThickness == new Thickness(1), "file search restores the normal border after hover");
            var list = (ListBox)window.FindName("TransferList");
            VerifyFileTableLayout(window, root, output);
            Check(list.Items.Count == 6 && !window.ViewModel.FilesAllDevices,
                "current device shows its six file records");
            Check(window.ViewModel.HasWorkspaceUnread && window.ViewModel.WorkspaceUnreadText == "3" &&
                ((FrameworkElement)window.FindName("MessageSearchButton")).Visibility == Visibility.Collapsed,
                "file workspace displays real unread count and hides message search");
            Check(((ComboBox)window.FindName("FileDeviceFilter")).Items[0].ToString() == "当前设备",
                "current-device option precedes all devices when a conversation exists");
            window.ViewModel.ShowFiles = false;
            ((RadioButton)window.FindName("MessagesViewButton")).IsChecked = true;
            window.OpenFileWorkspace(allDevices: true);
            Capture(root, output, "files-global-from-messages", 1180, 720);
            Check(list.Items.Count == 8 && window.ViewModel.FilesAllDevices && window.ViewModel.HighlightedPeerId is null,
                "global navigation survives the file radio Checked callback and includes other peers");
            window.OpenFileWorkspace(allDevices: false);
            Check(list.Items.Count == 6 && !window.ViewModel.FilesAllDevices,
                "returning to current device restores its scope");
            var query = (Wpf.Ui.Controls.TextBox)window.FindName("FileSearchInput");
            query.Text = "产品需求";
            Capture(root, output, "file-search-results", 1000, 600);
            Check(list.Items.Count == 2, "file search retains both active and completed matching records");
            Check(Descendants<TextBlock>(root).SelectMany(label => label.Inlines.OfType<System.Windows.Documents.Run>()).Count(run =>
                run.Text == "产品需求" && run.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(23, 107, 255)) == 2,
                "both matching file names highlight the query after it changes");
            query.Text = "年度报告";
            Capture(root, output, "file-search-empty", 1000, 600);
            Check(list.Items.Count == 0 && ((TextBlock)window.FindName("FileEmptyText")).Text.Contains("年度报告"),
                "empty search names the current query instead of claiming the history is empty");
            query.Text = "";
            Check(list.Items.Count == 6, "clearing a file query restores the current device records");
            window.ViewModel.ShowFiles = false;
            WaitForUiTask(window.ViewModel.SetWindowFocusAsync(true));
            Check(!window.ViewModel.HasWorkspaceUnread,
                "viewing the active conversation clears its persisted unread badge");
            var activePeer = window.ViewModel.ActivePeerId;
            window.ViewModel.DeviceQuery = "Galaxy Tab";
            Capture(root, output, "device-search-empty-minimum", 1000, 600);
            Check(window.ViewModel.ShowDeviceSearchEmpty &&
                ((FrameworkElement)window.FindName("DeviceGroupsScroll")).Visibility == Visibility.Collapsed &&
                window.ViewModel.ActivePeerId == activePeer,
                "an unmatched device query replaces the groups with one empty card and keeps the active conversation");
            window.ViewModel.DeviceQuery = "Galaxy";
            Layout(root, 1000, 600);
            Check(!window.ViewModel.ShowDeviceSearchEmpty && window.ViewModel.NearbyDevicesView.Count == 1,
                "a partial device match restores the groups and matching nearby device");
            window.ViewModel.DeviceQuery = "";
            Check(window.ViewModel.ConnectedDevicesView.Count == 2 && window.ViewModel.OfflineDevicesView.Count == 1,
                "clearing a device query restores all device groups");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "file scope verification creates no native window");
        }
        finally { WaitForUiTask(window.DisposeAsync().AsTask()); }
    }

    private void VerifyFileTableLayout(MainWindow window, FrameworkElement root, string output)
    {
        var list = (ListBox)window.FindName("TransferList");
        foreach (var (width, height, name) in new[] { (1180, 720, "default"), (1000, 600, "minimum"), (1600, 1000, "large") })
        {
            Layout(root, width, height);
            // Offscreen roots have no HWND, so explicitly deliver the container load lifecycle.
            foreach (var group in Descendants<GroupItem>(list)) group.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            foreach (var item in Descendants<ListBoxItem>(list)) item.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Capture(root, output, "file-table-details-" + name, width, height);
            var rows = Descendants<Border>(list).Where(border => border.Name == "FileTableRow").ToArray();
            var scroll = Descendants<ScrollViewer>(list).First();
            var viewport = Descendants<ScrollContentPresenter>(scroll).First();
            Check(rows.Length == 6, "all grouped rows are generated: " + name);
            Check(rows.All(row => Math.Abs(row.ActualWidth - viewport.ActualWidth) < 2 &&
                  Math.Abs(row.TranslatePoint(new Point(), viewport).X) < 1),
                "file rows fill the viewport without group indentation: " + name);
            var completeRows = rows.Where(row => row.DataContext is BlueLink.Domain.TransferItem { IsCompleted: true }).ToArray();
            Check(completeRows.Zip(completeRows.Skip(1)).All(pair => Math.Abs(
                    pair.Second.TranslatePoint(new Point(), root).Y - pair.First.TranslatePoint(new Point(0, pair.First.ActualHeight), root).Y) < 1),
                "consecutive file rows share a separator without card gaps: " + name);
            Check(rows.All(row => row.CornerRadius == new CornerRadius(0) && row.BorderThickness == new Thickness(0, 0, 0, 1)),
                "file rows use straight single bottom separators: " + name);
            Check(Descendants<Button>(list).All(button => button.BorderThickness == new Thickness(0)),
                "all file actions render without borders: " + name);
            var search = (FrameworkElement)window.FindName("FileSearchHost");
            var tabs = (FrameworkElement)window.FindName(((FrameworkElement)window.FindName("FileStatusFilter")).Visibility == Visibility.Visible ? "FileStatusFilter" : "FileStatusFilters");
            Check(Math.Abs(search.TranslatePoint(new Point(0, search.ActualHeight / 2), root).Y -
                           tabs.TranslatePoint(new Point(0, tabs.ActualHeight / 2), root).Y) < 1 && search.ActualWidth >= 180,
                "file search shares the status row and remains usable: " + name);
            var footerElements = new[] { "ReceiveDirectoryLabel", "ReceiveDirectoryPath", "ChangeReceiveDirectoryButton", "FileCountText" }
                .Select(key => (FrameworkElement)window.FindName(key)).ToArray();
            var centers = footerElements.Select(element => element.TranslatePoint(new Point(0, element.ActualHeight / 2), root).Y).ToArray();
            Check(centers.Max() - centers.Min() < 1 && ((Button)window.FindName("ChangeReceiveDirectoryButton")).BorderThickness == new Thickness(0),
                "receive path and borderless change action are vertically centered: " + name);
            var bar = Descendants<ScrollBar>(scroll).First(value => value.Orientation == Orientation.Vertical);
            if (bar.Visibility == Visibility.Visible)
                Check(viewport.TranslatePoint(new Point(viewport.ActualWidth, 0), scroll).X <= bar.TranslatePoint(new Point(), scroll).X + 1,
                    "scrollbar lane never overlays file rows: " + name);
            var header = (Grid)window.FindName("FileColumnsHeader");
            var cells = (Grid)rows[0].Child;
            Check(Math.Abs(header.TranslatePoint(new Point(), root).X - cells.TranslatePoint(new Point(), root).X) < 1 &&
                  Math.Abs(header.ActualWidth - cells.ActualWidth) < 2,
                "file column headings align with row cells: " + name);
        }
        var failedGroup = Descendants<GroupItem>(list).First(group =>
            group.DataContext is System.Windows.Data.CollectionViewGroup { Name: "失败" });
        var toggle = Descendants<ToggleButton>(failedGroup).First();
        var glyph = Descendants<Image>(toggle).First(image => image.Name == "FileGroupChevron");
        var presenter = Descendants<ItemsPresenter>(failedGroup).First();
        toggle.IsChecked = false;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(root, 1000, 600);
        Descendants<ScrollViewer>(list).First().ScrollToEnd();
        Capture(root, output, "file-table-failed-collapsed", 1000, 600);
        Check(presenter.Visibility == Visibility.Collapsed && glyph.RenderTransform is RotateTransform { Angle: -90 },
            "collapsed failed group hides rows and points its chevron right");
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(root, 1000, 600);
        Check(presenter.Visibility == Visibility.Visible && glyph.RenderTransform is RotateTransform { Angle: 0 },
            "reopened failed group restores rows and points its chevron down");
        VerifyResponsiveFileStatus(window, root, output);
        VerifyFileTableEdgeCases(window, root, output);
    }

    private void VerifyResponsiveFileStatus(MainWindow window, FrameworkElement root, string output)
    {
        var list = (ListBox)window.FindName("TransferList");
        var tabs = (StackPanel)window.FindName("FileStatusFilters");
        var selector = (ComboBox)window.FindName("FileStatusFilter");
        var search = (Wpf.Ui.Controls.TextBox)window.FindName("FileSearchInput");
        Layout(root, 1180, 720);
        Check(tabs.Visibility == Visibility.Visible && selector.Visibility == Visibility.Collapsed,
            "file status tabs appear when all filters fit");
        tabs.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "Completed")).IsChecked = true;
        search.Text = "产品";
        Capture(root, output, "file-filter-wide-completed", 1180, 720);
        Check(list.Items.Count == 1 && Equals(selector.SelectedValue, "Completed"),
            "status tabs and compact selector share the selected status and keyword");
        Capture(root, output, "file-filter-compact-completed", 1000, 600);
        Check(tabs.Visibility == Visibility.Collapsed && selector.Visibility == Visibility.Visible &&
              Equals(selector.SelectedValue, "Completed") && list.Items.Count == 1 && search.Text == "产品",
            "narrowing replaces tabs with a dropdown without resetting filters");
        selector.SelectedValue = "Failed";
        Capture(root, output, "file-filter-compact-failed", 1000, 600);
        Check(list.Items.Count == 1 && list.Items.Cast<BlueLink.Domain.TransferItem>().All(item => item.IsFailed) &&
              tabs.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "Failed")).IsChecked == true,
            "compact dropdown changes actual results and the hidden status tabs");
        Capture(root, output, "file-filter-wide-restored", 1180, 720);
        Check(tabs.Visibility == Visibility.Visible && selector.Visibility == Visibility.Collapsed &&
              tabs.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "Failed")).IsChecked == true,
            "widening restores the status tabs with the dropdown selection preserved");
        foreach (var width in new[] { 1000, 1180, 1050, 1600, 1000 })
        {
            Layout(root, width, 720);
            var toolbar = (Grid)window.FindName("FileToolbar");
            var controls = new FrameworkElement[] { selector.Visibility == Visibility.Visible ? selector : tabs,
                (FrameworkElement)window.FindName("FileSearchHost"), (FrameworkElement)window.FindName("FileDeviceFilter"),
                (FrameworkElement)window.FindName("FileDirectionFilter") };
            var bounds = controls.Select(control => control.TransformToAncestor(toolbar).TransformBounds(new Rect(control.RenderSize))).ToArray();
            Check(bounds.All(rect => rect.Left >= 0 && rect.Right <= toolbar.ActualWidth + 1) &&
                  bounds.Zip(bounds.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left + 1 &&
                      Math.Abs(pair.First.Top + pair.First.Height / 2 - pair.Second.Top - pair.Second.Height / 2) < 1),
                $"all file filters stay on one row without overlap at width {width}");
        }
        search.Text = "";
        selector.SelectedValue = "All";
        Check(list.Items.Count == 6 && selector.Items.OfType<ComboBoxItem>().First().Content as string == "全部状态" &&
              tabs.Children.OfType<RadioButton>().First().Content as string == "全部状态",
            "all-status label is explicit and resetting restores every record");
    }

    private void VerifyFileTableEdgeCases(MainWindow window, FrameworkElement root, string output)
    {
        var model = window.ViewModel;
        var original = model.Settings;
        var list = (ListBox)window.FindName("TransferList");
        var extraRows = Enumerable.Range(0, 16).Select(index => new BlueLink.Domain.TransferItem
        {
            Id = Guid.NewGuid(), Name = $"年度项目交付资料_含中文与English及非常长文件名_{index:D2}_最终确认归档版本.pdf",
            PeerName = "QA 超长设备名称用于检查来源目标不会挤占旁边的状态列", PeerId = model.ActivePeerId,
            TotalBytes = 9_876_543_210, CompletedBytes = 9_876_543_210, Outgoing = index % 2 == 0,
            Status = BlueLink.Domain.TransferStatus.Completed, CreatedAt = DateTimeOffset.Now.AddSeconds(index),
        }).ToArray();
        try
        {
            foreach (var row in extraRows) model.AllTransfers.Add(row);
            var longDirectory = Path.Combine(original.DownloadDirectory, "项目文档与图片归档", "跨设备接收文件", "需要省略显示但保留完整提示的超长目录名称");
            foreach (var (theme, language) in new[] { ("light", "zh-CN"), ("dark", "en-US") })
            {
                WaitForUiTask(model.SaveSettingsAsync(original with { Theme = theme, Language = language, DownloadDirectory = longDirectory }));
                Layout(root, 1000, 600);
                foreach (var group in Descendants<GroupItem>(list)) group.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                foreach (var item in Descendants<ListBoxItem>(list)) item.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var scroll = Descendants<ScrollViewer>(list).First();
                list.ScrollIntoView(extraRows[^1]);
                Capture(root, output, "file-table-long-names-" + theme, 1000, 600);
                scroll.ScrollToEnd();
                Capture(root, output, "file-table-long-content-" + theme, 1000, 600);
                var footer = (FrameworkElement)window.FindName("FileFooter");
                var path = (TextBlock)window.FindName("ReceiveDirectoryPath");
                var change = (Button)window.FindName("ChangeReceiveDirectoryButton");
                var count = (TextBlock)window.FindName("FileCountText");
                Check(path.Text == longDirectory && Equals(path.ToolTip, longDirectory) && path.TextTrimming == TextTrimming.CharacterEllipsis,
                    "long receive path keeps full text in tooltip: " + theme);
                Check(change.TranslatePoint(new Point(change.ActualWidth, 0), footer).X <= count.TranslatePoint(new Point(), footer).X &&
                      footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y <= 600,
                    "long path never covers change action or file count, footer stays fixed: " + theme);
                var rows = Descendants<Border>(list).Where(border => border.Name == "FileTableRow").ToArray();
                Check(rows.Count(row => extraRows.Contains(row.DataContext)) == extraRows.Length &&
                    rows.Where(row => extraRows.Contains(row.DataContext)).All(row =>
                    Descendants<TextBlock>(row).Any(text => Equals(text.ToolTip, ((BlueLink.Domain.TransferItem)row.DataContext).Name) &&
                        text.TextTrimming == TextTrimming.CharacterEllipsis)), "long file names keep readable columns and full tooltips: " + theme);
                var search = (FrameworkElement)window.FindName("FileSearchHost");
                var tabs = (FrameworkElement)window.FindName(((FrameworkElement)window.FindName("FileStatusFilter")).Visibility == Visibility.Visible ? "FileStatusFilter" : "FileStatusFilters");
                Check(search.ActualWidth >= 180 && Math.Abs(search.TranslatePoint(new Point(0, search.ActualHeight / 2), root).Y -
                      tabs.TranslatePoint(new Point(0, tabs.ActualHeight / 2), root).Y) < 1, "localized file toolbar stays on one search row: " + theme);
                Check(scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                    "long file list scrolls fully while footer remains visible: " + theme);
                foreach (var name in new[] { "FileStatusFilter", "FileDeviceFilter", "FileDirectionFilter" })
                {
                    var combo = (ComboBox)window.FindName(name);
                    var caption = Descendants<TextBlock>(combo).FirstOrDefault(text => text.Text == combo.Text);
                    Check(caption is not null, "selected filter caption is rendered: " + name + "/" + theme);
                    var textSize = new FormattedText(caption!.Text, BlueLink.Localization.Strings.Culture,
                        caption.FlowDirection, new Typeface(caption.FontFamily, caption.FontStyle, caption.FontWeight, caption.FontStretch),
                        caption.FontSize, caption.Foreground, 1);
                    Check(textSize.Width <= caption.ActualWidth + 1, "localized filter caption fits without clipping: " + name + "/" + theme);
                }
            }
        }
        finally
        {
            foreach (var row in extraRows) model.AllTransfers.Remove(row);
            WaitForUiTask(model.SaveSettingsAsync(original));
            Descendants<ScrollViewer>(list).First().ScrollToTop();
            Layout(root, 1000, 600);
        }
    }

    private void VerifyMessageSearchScenes(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "message-search-scenes");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        MessageSearchWindow? search = null;
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "message-history"));
            var attachment = main.ViewModel.Messages.SelectMany(message => message.Attachments ?? [])
                .Single(item => item.IsImage && item.CanOpen);
            var attachmentList = new ListBox { DataContext = main.ViewModel,
                Style = (Style)main.FindResource("DefaultListBoxStyle"),
                ItemContainerStyle = (Style)main.FindResource("TransparentListItemStyle"),
                ItemTemplate = (DataTemplate)main.FindResource("AttachmentTemplate"), ItemsSource = new[] { attachment } };
            Capture(attachmentList, output, "message-image-completed", 420, 260);
            var thumbnail = Descendants<Image>(attachmentList).Single(item => item.Name == "Thumbnail");
            Check(thumbnail.Source is not null && thumbnail.Visibility == Visibility.Visible &&
                thumbnail.ActualWidth <= 240 && thumbnail.ActualHeight <= 144 &&
                Descendants<Border>(attachmentList).Single(item => item.Name == "ImageProgress").Visibility == Visibility.Collapsed,
                "completed image uses a compact thumbnail without an extra filename caption");
            attachmentList.ItemsSource = new[] { attachment with { State = "Transferring", CompletedBytes = 100 } };
            Capture(attachmentList, output, "message-image-transferring", 420, 320);
            Check(Descendants<Border>(attachmentList).Single(item => item.Name == "ImageProgress").Visibility == Visibility.Visible &&
                Descendants<Wpf.Ui.Controls.ProgressRing>(attachmentList).Any(item => !item.IsIndeterminate && item.Progress > 0),
                "an image transfer displays actual progress over its thumbnail using an official progress ring");
            attachmentList.ItemsSource = new[] { attachment with { LocalPath = Path.Combine(directory, "missing-QA.png"), PreviewPath = null } };
            Capture(attachmentList, output, "message-image-unavailable", 420, 260);
            Check(Descendants<Grid>(attachmentList).Single(item => item.Name == "FileCard").Visibility == Visibility.Visible &&
                Descendants<Image>(attachmentList).Single(item => item.Name == "Thumbnail").Visibility == Visibility.Collapsed,
                "a missing image falls back to a readable file card instead of an empty bubble");
            VerifyAttachmentCardStates(main, attachment, output);
            search = new MessageSearchWindow(main.ViewModel.ActivePeerTitle, main.ViewModel.Messages);
            var root = DetachForRendering(search);
            var list = (ListBox)search.FindName("Results");
            foreach (var (kind, count) in new[] { (BlueLink.Domain.HistoryKind.All, 10), (BlueLink.Domain.HistoryKind.Text, 4),
                (BlueLink.Domain.HistoryKind.Images, 1), (BlueLink.Domain.HistoryKind.Files, 5) })
            {
                search.ApplyFilter("", kind);
                Capture(root, output, "search-kind-" + kind, 860, 640);
                Check(list.Items.Count == count, "message type filter preserves the expected matching records: " + kind);
                if (kind == BlueLink.Domain.HistoryKind.Images)
                    Check(Descendants<TextBlock>(root).Any(label => label.Text.Contains("240 × 144")),
                        "image search shows dimensions read from the actual file instead of the downscaled thumbnail");
            }
            search.ApplyFilter("需求文档", BlueLink.Domain.HistoryKind.All);
            Check(list.Items.Count == 3, "search matches text and file names together");
            search.ApplyFilter("安装手册", BlueLink.Domain.HistoryKind.All);
            Capture(root, output, "search-keyword-empty", 620, 480);
            Check(list.Items.Count == 0 && ((TextBlock)search.FindName("EmptyMessage")).Text.Contains("安装手册") &&
                ((FrameworkElement)search.FindName("EmptyState")).Visibility == Visibility.Visible,
                "message search empty state includes the current query at minimum size");
            search.ApplyFilter("", BlueLink.Domain.HistoryKind.Date, DateTime.Today);
            Capture(root, output, "search-calendar-minimum", 620, 480);
            Check(list.Items.Count == 10 && search.MatchingDates.Contains(DateTime.Today), "calendar marks dates with real matching records");
            var calendar = (FrameworkElement)search.FindName("CalendarPane");
            Check(calendar.Visibility == Visibility.Visible && calendar.ActualWidth >= 230 && list.ActualWidth >= 270,
                "calendar and search results remain visible together at minimum size");
            var viewport = (Viewbox)search.FindName("CalendarViewport");
            var dayButtons = Descendants<CalendarDayButton>(viewport).Where(day => day.Visibility == Visibility.Visible).ToArray();
            var todayButton = dayButtons.Single(day => day.DataContext is DateTime date && date == DateTime.Today);
            var todayMarker = Descendants<System.Windows.Shapes.Ellipse>(todayButton).Single(marker => marker.Name == "RecordDateMarker");
            Check(todayMarker.Visibility == Visibility.Visible && todayMarker.ActualWidth >= 4 &&
                todayMarker.Fill is SolidColorBrush markerBrush && markerBrush.Color == Colors.White,
                "matching date marker is rendered and contrasts with the blue today background");
            Check(dayButtons.Length >= 35 && dayButtons.All(day =>
            {
                var bounds = day.TransformToAncestor(viewport).TransformBounds(new Rect(day.RenderSize));
                return bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
            }), "all calendar day buttons fit the scaled viewport without right or bottom clipping");
            search.ApplyFilter("", BlueLink.Domain.HistoryKind.Date, DateTime.Today.AddDays(-1));
            Check(list.Items.Count == 0 && search.MatchingDates.Contains(DateTime.Today),
                "choosing an empty date retains markers for dates containing matches");
            Layout(root, 620, 480);
            Check(todayMarker.Visibility == Visibility.Visible && todayMarker.Fill is SolidColorBrush unselectedBrush &&
                unselectedBrush.Color == Colors.White, "today's marker stays visible when another day is selected");
            search.ApplyFilter("no-matching-QA-record", BlueLink.Domain.HistoryKind.Date, DateTime.Today);
            Layout(root, 620, 480);
            Check(todayMarker.Visibility == Visibility.Collapsed, "changing the query removes stale date markers");
            Check(new WindowInteropHelper(search).Handle == IntPtr.Zero, "message search verification creates no native window");
        }
        finally { search?.Close(); WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyAttachmentCardStates(MainWindow main, BlueLink.Domain.ChatAttachment image, string output)
    {
        var file = main.ViewModel.Messages.SelectMany(message => message.Attachments ?? [])
            .First(attachment => !attachment.IsImage && attachment.CanOpen);
        var list = new ListBox { DataContext = main.ViewModel,
            Style = (Style)main.FindResource("DefaultListBoxStyle"),
            ItemContainerStyle = (Style)main.FindResource("TransparentListItemStyle"),
            ItemTemplate = (DataTemplate)main.FindResource("MessageTemplate") };
        foreach (var (scene, fixture) in DesktopAcceptance.MenuFixtures.Where(pair => !pair.Key.StartsWith("files-", StringComparison.Ordinal)))
        {
            var original = fixture.Image ? image : file;
            var attachment = original with { State = fixture.State, CompletedBytes = (long)(original.Size * .68) };
            list.ItemsSource = new[] { new BlueLink.Domain.ChatItem(Guid.NewGuid(), "", fixture.Outgoing,
                DateTimeOffset.Now, BlueLink.Domain.MessageStatus.Delivered,
                fixture.Image ? BlueLink.Domain.ChatItemKind.Image : BlueLink.Domain.ChatItemKind.File, [attachment]) };
            Capture(list, output, "card-" + scene, 560, 260);
            var card = Descendants<Grid>(list).Single(element => element.Name == "FileCard");
            var border = Descendants<Border>(list).Single(element => element.Name == "AttachmentBorder");
            var imageProgress = Descendants<Border>(list).Single(element => element.Name == "ImageProgress");
            var fileProgress = Descendants<Grid>(list).Single(element => element.Name == "FileProgress");
            if (fixture.Image)
            {
                Check(card.Visibility == Visibility.Collapsed && border.ActualWidth <= 240 && border.ActualHeight <= 144,
                    "image states keep a compact thumbnail without a filename panel: " + scene);
                Check((imageProgress.Visibility == Visibility.Visible) == (fixture.State == "Transferring"),
                    "image progress appears only while the fixture is transferring: " + scene);
            }
            else
            {
                var expectedState = fixture.State switch
                {
                    "Offered" => "等待接收",
                    "Transferring" => fixture.Outgoing ? "传输中" : "接收中",
                    "Paused" => fixture.Outgoing ? "传输已暂停" : "已暂停",
                    "Failed" => fixture.Outgoing ? "传输失败" : "接收失败",
                    "Completed" => fixture.Outgoing ? "已发送" : "已接收",
                    _ => throw new InvalidOperationException("Unexpected attachment state")
                };
                Check(border.ActualWidth <= 388 && border.ActualHeight is >= 60 and <= 76 &&
                    Descendants<TextBlock>(list).Single(element => element.Name == "FileSummary").Text == attachment.SizeText + " · " + expectedState,
                    "file states use the compact prototype dimensions and directional status: " + scene);
                Check((fileProgress.Visibility == Visibility.Visible) == (fixture.State == "Transferring") &&
                    (Descendants<Image>(list).Single(element => element.Name == "PausedReceiveIndicator").Visibility == Visibility.Visible) ==
                        (fixture.State == "Paused" && !fixture.Outgoing),
                    "active and paused files show the appropriate prototype indicator: " + scene);
            }
            var ring = Descendants<Wpf.Ui.Controls.ProgressRing>(fixture.Image ? (DependencyObject)imageProgress : fileProgress).Single();
            Check(!ring.IsIndeterminate && Math.Abs(ring.Progress - attachment.Progress * 100) < .01 && ring.EngAngle > 230 && ring.EngAngle < 260,
                "the official ring displays actual determinate transfer progress: " + scene);
        }
    }

    private void VerifyDeviceTransferStates(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "device-transfer-states");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "files-device-transfer"));
            var root = DetachForRendering(main);
            var peer = main.ViewModel.ConnectedConversations.Single(item => item.PeerName.Contains("SURFACE"));
            var active = peer.ActiveTransfer ?? throw new InvalidOperationException("Device progress did not bind to its transfer");
            var selectedPeerId = main.ViewModel.ActivePeerId;
            Capture(root, output, "device-transfer-active", 1180, 720);
            var card = Descendants<Border>(root).Single(item => item.Name == "ConversationCard" && ReferenceEquals(item.DataContext, peer));
            var detail = Descendants<TextBlock>(card).Single(item => item.Name == "ConversationSecondaryText");
            var progress = Descendants<ProgressBar>(card).Single(item => item.Name == "ConversationTransferProgress");
            Check(detail.Text == "正在接收文件 · 68%" && progress.Visibility == Visibility.Visible && card.ActualHeight >= 72,
                "a connected device shows the real incoming transfer and progress in the prototype card");
            active.CompletedBytes = active.TotalBytes / 2;
            Layout(root, 1180, 720);
            Check(detail.Text == "正在接收文件 · 50%" && Math.Abs(progress.Value - .5) < .01,
                "device text and progress update when transferred bytes change");
            active.Status = BlueLink.Domain.TransferStatus.Paused;
            Capture(root, output, "device-transfer-paused", 1180, 720);
            Check(detail.Text == "已暂停接收 · 50%", "the device card distinguishes a paused receive");
            active.Status = BlueLink.Domain.TransferStatus.Completed;
            Layout(root, 1180, 720);
            Check(!peer.HasActiveTransfer && progress.Visibility == Visibility.Collapsed && detail.Text == peer.LastSeenText,
                "completing the device transfer restores the normal connection status");
            var outgoing = new BlueLink.Domain.TransferItem { Id = Guid.NewGuid(), Name = "QA outgoing.txt", PeerId = peer.PeerId,
                TotalBytes = 100, CompletedBytes = 25, Outgoing = true, Status = BlueLink.Domain.TransferStatus.Transferring };
            main.ViewModel.AllTransfers.Add(outgoing);
            Layout(root, 1180, 720);
            Check(ReferenceEquals(peer.ActiveTransfer, outgoing) && detail.Text == "正在发送文件 · 25%" &&
                main.ViewModel.ConnectedConversations.Contains(peer) && main.ViewModel.ActivePeerId == selectedPeerId,
                "a new outgoing transfer updates the existing card without changing the selected conversation");
            main.ViewModel.AllTransfers.Remove(outgoing);
            Check(!peer.HasActiveTransfer && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                "removing the transfer clears the card without opening a native window");
            WaitForUiTask(main.OpenConversationAsync(peer));
            Check(!main.ViewModel.ShowFiles && main.ViewModel.ActivePeerId == peer.PeerId &&
                ((RadioButton)main.FindName("MessagesViewButton")).IsChecked == true,
                "the device menu opens the requested message conversation from the file workspace");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyFeedbackStates(SettingsPage settings, FrameworkElement content, string output)
    {
        var description = (Wpf.Ui.Controls.TextBox)settings.FindName("FeedbackDescription");
        var category = (ComboBox)settings.FindName("FeedbackCategory");
        var diagnostics = (Wpf.Ui.Controls.ToggleSwitch)settings.FindName("FeedbackDiagnostics");
        var generate = (Button)settings.FindName("GenerateFeedbackButton");
        var progress = (ProgressBar)settings.FindName("FeedbackProgress");
        var back = (Button)settings.FindName("HelpBackButton");
        var message = (TextBlock)settings.FindName("FeedbackStatusMessage");
        foreach (var scene in DesktopAcceptance.FeedbackScenes.Where(value => !value.StartsWith("feedback-ready-")))
        {
            settings.ApplyFeedbackFixture(scene);
            Capture(content, output, scene + "-prototype", 1600, 1000);
            if (scene.StartsWith("help-"))
            {
                var questions = Descendants<Expander>((StackPanel)settings.FindName("HelpDetail")).ToArray();
                Check(questions.Length == 4 && questions.Count(value => value.IsExpanded) == 1,
                    "each help topic starts with four questions and one open answer: " + scene);
                questions[1].IsExpanded = true;
                Layout(content, 1000, 600);
                Check(questions[1].IsExpanded && Descendants<TextBlock>(questions[1]).Any(value => value.Text.Length > 30),
                    "expanding another question preserves a readable answer at minimum width: " + scene);
                continue;
            }
            var generating = scene.Contains("-generating-");
            if (generating)
            {
                Check(Descendants<FrameworkElement>(progress).Single(value => value.Name == "PART_GlowRect").ActualHeight >= 2,
                    "official indeterminate progress template keeps a visible two-DIP indicator: " + scene);
            }
            Check(System.Windows.Automation.AutomationProperties.GetName(generate) == (string)generate.Content,
                "feedback action accessibility name follows the visible generation state: " + scene);
            Check(settings.CanLeave != generating && back.IsEnabled != generating &&
                  ((Button)settings.FindName("ReturnToHomeButton")).IsEnabled != generating &&
                  category.IsEnabled != generating && description.IsEnabled != generating &&
                  diagnostics.IsEnabled != generating && generate.IsEnabled != generating &&
                  (progress.Visibility == Visibility.Visible) == generating,
                "generation locks form and navigation together and exposes progress: " + scene);
            if (scene.Contains("-failure-"))
                Check(description.Text == SettingsPage.AcceptanceFeedbackDraft(scene).Description &&
                      diagnostics.IsChecked == scene.EndsWith("-on") && (string)generate.Content == "重试生成" &&
                      message.Text.Contains("已保留") && message.ToolTip is string,
                    "failure keeps the complete draft and actual error detail available for retry: " + scene);
            if (scene == "feedback-validation")
            {
                Check(((TextBlock)settings.FindName("FeedbackValidationMessage")).Visibility == Visibility.Visible &&
                      ((TextBlock)settings.FindName("FeedbackDescriptionLabel")).Text.Contains("必填"),
                    "empty feedback has an in-field required message");
                description.Text = "有效的问题描述";
                Check(((TextBlock)settings.FindName("FeedbackValidationMessage")).Visibility == Visibility.Collapsed,
                    "typing a nonblank description clears the required error");
            }
        }
        settings.ApplyFeedbackFixture("feedback-filled-on");
        var scroll = (ScrollViewer)settings.FindName("HelpDetailScroll");
        Layout(content, 1000, 600);
        scroll.ScrollToEnd();
        Capture(content, output, "feedback-minimum-bottom", 1000, 600);
        var counter = (TextBlock)settings.FindName("FeedbackCharacterCount");
        var editRegion = (Border)settings.FindName("FeedbackDescriptionBorder");
        Check(counter.TranslatePoint(new Point(0, counter.ActualHeight), editRegion).Y < editRegion.ActualHeight &&
              generate.TranslatePoint(new Point(0, generate.ActualHeight), scroll).Y <= scroll.ActualHeight,
            "counter stays inside the editor and scrolling reveals generation at minimum size");
        foreach (var includeDiagnostics in new[] { false, true })
        {
            var ready = new FeedbackReadyWindow(new(Path.Combine(Path.GetTempPath(),
                new string('x', 90), "BlueLink-feedback-QA.zip"), includeDiagnostics));
            var root = DetachForRendering(ready);
            Capture(root, output, "feedback-ready-" + (includeDiagnostics ? "on" : "off"), 560, 360);
            Check(((TextBlock)ready.FindName("PackageDirectory")).TextWrapping == TextWrapping.NoWrap &&
                  ((TextBlock)ready.FindName("PackageDirectory")).ToolTip is string &&
                  ((TextBlock)ready.FindName("DiagnosticsNotice")).Text.Contains("不包含聊天内容和用户文件") &&
                  Descendants<Button>(root).All(button => button.TranslatePoint(new Point(0, button.ActualHeight), root).Y <= 360),
                "ready dialog preserves the full path in a tooltip and keeps all actions in bounds: " + includeDiagnostics);
            ready.Close();
        }
        settings.SetFeedbackState(SettingsPage.FeedbackPresentationState.Idle);
    }

    private void VerifyConfirmationDialogs(string output)
    {
        var documents = new[] {
            ("remove-trust", ConfirmationDocument.RemoveTrust("QA Galaxy S24")),
            ("clear-conversation", ConfirmationDocument.ClearConversation("QA Galaxy S24")),
            ("delete-record", ConfirmationDocument.DeleteRecord("确定删除这条本机消息记录吗？")),
            ("cancel-send", ConfirmationDocument.CancelTransfer("QA 文档.pdf", true)),
            ("cancel-receive", ConfirmationDocument.CancelTransfer("QA 视频.mp4", false)),
            ("clear-all-conversations", ConfirmationDocument.ClearAll(false)),
            ("clear-all-files", ConfirmationDocument.ClearAll(true)) };
        foreach (var (name, document) in documents)
        {
            var window = new ConfirmationWindow(document);
            var root = DetachForRendering(window);
            Capture(root, output, "confirmation-" + name, 460, 250);
            var primary = (Button)window.FindName("ConfirmationPrimaryButton");
            var cancel = (Button)window.FindName("ConfirmationCancelButton");
            Check(Descendants<Button>(root).Count(b => b.Visibility == Visibility.Visible) == 3 && primary.ActualHeight == 36 && cancel.ActualHeight == 36 &&
                cancel.TransformToAncestor(root).Transform(new Point()).X < primary.TransformToAncestor(root).Transform(new Point()).X,
                "confirmation has a close button, then cancel and specific primary actions in prototype order: " + name);
            var question = (TextBlock)window.FindName("ConfirmationQuestion");
            var impact = (TextBlock)window.FindName("ConfirmationImpact");
            Check(question.ActualHeight <= 44 && impact.ActualHeight <= 36 &&
                primary.Background is SolidColorBrush brush && brush.Color == ((SolidColorBrush)Application.Current.FindResource("ConfirmationDangerBrush")).Color,
                "confirmation content fits and the primary action uses the prototype danger color: " + name);
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!window.Confirmed && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "cancel does not confirm or create a desktop window in background validation: " + name);
        }
        var confirm = new ConfirmationWindow(documents[2].Item2);
        ((Button)confirm.FindName("ConfirmationPrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(confirm.Confirmed, "only the explicit primary confirmation returns an accepted result");
        Check(documents[3].Item2.PrimaryText == "取消传输" && documents[4].Item2.PrimaryText == "取消接收" &&
            documents[4].Item2.Question.Contains("接收"), "transfer confirmation preserves outgoing and incoming action semantics");
    }

    private void VerifySettingsDialogs(MainViewModel model, string output)
    {
        var name = new DeviceNameWindow(model);
        var nameRoot = DetachForRendering(name);
        Capture(nameRoot, output, "settings-device-name", 520, 286);
        var input = (Wpf.Ui.Controls.TextBox)name.FindName("DeviceNameInput");
        var save = (Button)name.FindName("SaveNameButton");
        Check(input.ActualHeight == 44 && input.MaxLength == 32 && save.TransformToAncestor(nameRoot).TransformBounds(new Rect(save.RenderSize)).Bottom <= 263,
            "device name uses the prototype input height, character limit and compact footer");
        name.Close();

        var peers = new List<BlueLink.Domain.ConversationSummary>
        {
            new("00000000000000000000000000000001", "QA REDMI K80 Pro", BlueLink.Domain.PeerPlatform.Android, BlueLink.Domain.DeviceAvailability.Offline, null, "", 0, DateTimeOffset.Now),
            new("00000000000000000000000000000002", "QA SURFACE-LAPTOP", BlueLink.Domain.PeerPlatform.Windows, BlueLink.Domain.DeviceAvailability.Offline, null, "", 0, DateTimeOffset.Now),
        };
        var removal = new RemoveTrustedDevicesWindow(peers);
        peers.Clear();
        var removalRoot = DetachForRendering(removal);
        Capture(removalRoot, output, "settings-remove-trusted", 560, 382);
        Check(removal.Peers.Count == 2 && removal.Description.Contains("2"), "trust removal shows a stable snapshot of exactly the peers being confirmed");
        Check(((ScrollViewer)removal.FindName("TrustedPeersScroll")).ScrollableHeight < 1 &&
            ((Button)removal.FindName("RemoveTrustConfirmButton")).ActualWidth == 96, "two trusted peers fit the prototype list and removal action");
        var longList = new RemoveTrustedDevicesWindow(Enumerable.Repeat(removal.Peers[0], 8));
        var longRoot = DetachForRendering(longList);
        Capture(longRoot, output, "settings-remove-trusted-scroll", 560, 382);
        Check(((ScrollViewer)longList.FindName("TrustedPeersScroll")).ScrollableHeight > 0 &&
            ((Button)longList.FindName("RemoveTrustConfirmButton")).TransformToAncestor(longRoot).TransformBounds(new Rect(((Button)longList.FindName("RemoveTrustConfirmButton")).RenderSize)).Bottom < 382,
            "large trusted lists scroll while confirmation stays visible");
        removal.Close(); longList.Close();
        var empty = new RemoveTrustedDevicesWindow([]);
        Check(!((Button)empty.FindName("RemoveTrustConfirmButton")).IsEnabled, "empty trust list cannot confirm removal");
        empty.Close();
        var reset = new ResetIdentityWindow();
        var resetRoot = DetachForRendering(reset);
        Capture(resetRoot, output, "settings-reset-identity", 540, 320);
        var resetButton = (Button)reset.FindName("ResetConfirmButton");
        Check(resetButton.ActualWidth == 88 && ((Button)reset.FindName("ResetCancelButton")).ActualWidth == 96 &&
            ((SolidColorBrush)resetButton.Background).Color == ((SolidColorBrush)Application.Current.FindResource("SurfaceBrush")).Color,
            "identity reset is an outlined destructive action after a wider cancel button");
        Check(new WindowInteropHelper(reset).Handle == IntPtr.Zero, "settings dialog verification creates no native window");
        reset.Close();
    }

    private void VerifyInformationDialogs(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "information-dialogs");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "files-current"));
            var model = main.ViewModel;
            var nearby = model.DescribeDevice(model.NearbyNewDevices.Single());
            Check(nearby.Fields.Single(item => item.Label == "信任状态").Value == "未信任" &&
                nearby.Fields.Single(item => item.Label == "蓝牙信号").Value == "-58 dBm" &&
                nearby.Fields.Single(item => item.Label == "最后连接").Value == "未记录",
                "nearby device information reports real discovery data without inventing trust or a previous connection");
            var connected = model.DescribeDevice(model.ConnectedConversations.First());
            Check(connected.Fields.Single(item => item.Label == "连接状态") is { Value: "已连接", Tone: "Success" } &&
                connected.Fields.Single(item => item.Label == "蓝牙信号").Value == "未记录",
                "connected device information distinguishes connection state from unavailable signal data");
            var documents = new List<(string Name, InformationDocument Document)> { ("device", nearby) };
            foreach (var (name, item, failure) in new[] {
                ("attachment", model.AllTransfers.First(item => item.Status == BlueLink.Domain.TransferStatus.Completed && item.MimeType == "application/pdf"), false),
                ("image", model.AllTransfers.First(item => item.MimeType == "image/png"), false),
                ("failure", model.AllTransfers.First(item => item.IsFailed), true) })
            {
                var attachment = new BlueLink.Domain.ChatAttachment(Guid.NewGuid(), item.Id, item.Name, item.MimeType,
                    item.TotalBytes, item.LocalPath, item.Status.ToString(), CompletedBytes: item.CompletedBytes);
                var pending = model.DescribeAttachmentAsync(attachment, item, failure);
                WaitForUiTask(pending);
                var document = pending.GetAwaiter().GetResult();
                documents.Add((name, document));
                if (failure)
                    Check(document.Fields.Single(row => row.Label == "错误代码").Value == "QA_TIMEOUT" &&
                        document.Fields.Single(row => row.Label == "失败原因").Value == "QA 测试：对端连接中断" &&
                        document.Fields.Single(row => row.Label == "失败阶段").Value == "未记录",
                        "failure information preserves persisted error details and does not invent a failure stage");
                else
                    Check(document.Fields.Single(row => row.Label == "传输方向").Value == (item.Outgoing ? "发送" : "接收") &&
                        document.Fields.Single(row => row.Label == (item.Outgoing ? "目标设备" : "来源设备")).Value == "QA REDMI K80 Pro",
                        "attachment information reports the real direction and peer: " + name);
                if (attachment.IsImage)
                    Check(document.Fields.Single(row => row.Label == "图片尺寸").Value == "240 × 144", "image information reads the actual bitmap dimensions");
                else if (!failure)
                    Check(document.Fields.Single(row => row.Label == "文件校验").Value == "未记录", "file existence is not reported as successful checksum verification");
            }
            foreach (var (name, document) in documents)
            {
                var window = new InformationWindow(document);
                var root = DetachForRendering(window);
                Capture(root, output, "information-" + name, 520, (int)document.Height);
                var values = Descendants<TextBlock>(root).Where(item => item.Name == "InformationValue").ToList();
                Check(values.Count == document.Fields.Count && values.All(item => item.TranslatePoint(new Point(), root).Y + item.ActualHeight <= document.Height - 18),
                    "all information fields fit inside the prototype dialog: " + name);
                Check(Descendants<Button>(root).Count() == 1 && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                    "information dialog has only its close button and creates no native window during background validation: " + name);
                window.Close();
            }
            var sample = new BlueLink.Domain.ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "QA.txt", "text/plain", 42, State: "Completed");
            var record = new BlueLink.Storage.StoredTransfer(sample.TransferId.ToString("N"), "peer", null, "Incoming", "Completed",
                sample.FileName, sample.MimeType, 42, 42, null, null, new byte[32], null, null, 1_000, 90_000);
            var completed = MainViewModel.AttachmentInformation(sample, false, "peer", record);
            Check(completed.Fields.Single(row => row.Label == "完成时间").Value == DateTimeOffset.FromUnixTimeMilliseconds(90_000).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") &&
                completed.Fields.Single(row => row.Label == "文件校验").Value == "传输时已校验", "completion uses the stored completion update and recorded checksum, not creation time");
            var incomplete = MainViewModel.AttachmentInformation(sample with { State = "Transferring" }, null, null, record);
            Check(incomplete.Fields.Where(row => row.Label is "完成时间" or "文件校验" or "传输方向").All(row => row.Value == "未记录"),
                "incomplete and missing metadata remain explicitly unknown");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyFileContextMenus(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "file-context-menus");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "files-current"));
            var root = DetachForRendering(main);
            Layout(root, 1180, 720);
            var fileMenu = Descendants<Border>(root).First(border => border.ContextMenu is not null &&
                border.DataContext is BlueLink.Domain.TransferItem).ContextMenu;
            var selectedFileRow = Descendants<ListBoxItem>(root).First(row => row.DataContext is BlueLink.Domain.TransferItem);
            selectedFileRow.IsSelected = true;
            Layout(root, 1180, 720);
            var selectedTransfer = (BlueLink.Domain.TransferItem)selectedFileRow.DataContext;
            Check(Descendants<TextBlock>(selectedFileRow).Where(text => text.Text == selectedTransfer.SizeText || text.Text == selectedTransfer.RouteText)
                .All(text => text.Foreground is SolidColorBrush brush && brush.Color == ((SolidColorBrush)root.FindResource("InkBrush")).Color),
                "right-click file selection keeps its text readable on the pale selected background");
            var attachmentControl = (System.Windows.Controls.Button)((DataTemplate)main.FindResource("AttachmentTemplate")).LoadContent();
            var attachmentMenu = attachmentControl.ContextMenu;
            var file = main.ViewModel.Messages.SelectMany(message => message.Attachments ?? [])
                .First(item => item.CanOpen && !item.IsImage);
            foreach (var (scene, fixture) in DesktopAcceptance.MenuFixtures)
            {
                var attachmentScene = !scene.StartsWith("files-", StringComparison.Ordinal);
                var menu = attachmentScene ? attachmentMenu : fileMenu;
                object source = attachmentScene ? file with { State = fixture.State,
                    MimeType = fixture.Image ? "image/png" : "application/pdf" }
                    : new BlueLink.Domain.TransferItem { Id = Guid.NewGuid(), Name = fixture.Name,
                        TotalBytes = file.Size, Outgoing = fixture.Outgoing, LocalPath = file.LocalPath,
                        Status = Enum.Parse<BlueLink.Domain.TransferStatus>(fixture.State) };
                MainWindow.ConfigureFileContextMenu(menu, source, fixture.Outgoing);
                var suffix = fixture.Outgoing ? "传输" : "接收";
                var expected = fixture.State switch
                {
                    "Offered" => new[] { "查看文件详情", "取消" + suffix },
                    "Transferring" => new[] { "查看文件详情", "暂停" + suffix, "取消" + suffix },
                    "Paused" => new[] { "查看文件详情", "继续" + suffix, "取消" + suffix },
                    "Failed" => new[] { "查看文件信息", "查看失败原因", "删除本机记录" },
                    "Completed" => new[] { fixture.Image ? "图片预览" : "打开", "复制文件", "在文件夹中显示", "另存为",
                        "查看文件详情", attachmentScene ? "删除本机消息" : "删除本机记录" },
                    _ => throw new InvalidOperationException("Unexpected menu fixture state.")
                };
                menu.Visibility = Visibility.Visible;
                // The official WPF UI popup includes 30 DIP of shadow on each side.
                // Include it in the offscreen canvas and verify every action remains in bounds.
                menu.Width = 300;
                menu.Height = expected.Length * 36 + 90;
                Capture(menu, output, scene, 300, expected.Length * 36 + 90);
                var visibleItems = menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).ToArray();
                Check(visibleItems.All(item =>
                {
                    var bounds = item.TransformToAncestor(menu).TransformBounds(new Rect(item.RenderSize));
                    return bounds.Top >= 0 && bounds.Bottom <= menu.ActualHeight && bounds.Left >= 0 && bounds.Right <= menu.ActualWidth;
                }), "every visible menu action is inside the rendered popup bounds: " + scene);
                var separator = menu.Items.OfType<Separator>().Single(item => item.Visibility == Visibility.Visible);
                Check(Descendants<Border>(separator).Any(line => line.ActualHeight >= 1 && line.ActualWidth > 100),
                    "the separator before the destructive action has a visible line: " + scene);
                Check(visibleItems.Select(item => item.Header as string).SequenceEqual(expected),
                    "prototype menu labels/order match the file direction and state: " + scene);
                Check(visibleItems.All(item => item.IsEnabled && item.ActualHeight >= 36) &&
                    visibleItems.Where(item => item.CommandParameter as string == "danger").All(item =>
                        item.Foreground is SolidColorBrush brush && brush.Color == ((SolidColorBrush)root.FindResource("DangerBrush")).Color),
                    "visible menu actions remain usable and destructive labels are red: " + scene);
            }
            Check(!fileMenu.IsOpen && !attachmentMenu.IsOpen && PresentationSource.FromVisual(fileMenu) is null &&
                PresentationSource.FromVisual(attachmentMenu) is null && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                "context menu verification never opens a native popup or sends a transfer command");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyTransientToasts(string dataRoot, string output)
    {
        var main = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(dataRoot, "toast-layout"));
        try
        {
            WaitForUiTask(main.ViewModel.InitializeLocalStateAsync());
            main.LoadVisualFixture();
            var root = DetachForRendering(main);
            var host = (ToastHost)main.FindName("Toasts");
            foreach (var fixture in DesktopAcceptance.ToastFixtures)
            {
                host.Items.Clear();
                host.Show(fixture.Message, fixture.Level, TimeSpan.FromMinutes(1));
                Capture(root, output, fixture.Scene, 1180, 720);
                var card = Descendants<Border>(host).Single(border => border.Name == "ToastCard");
                var bounds = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
                Check(Math.Abs(card.ActualHeight - 48) < 1 && card.ActualWidth < 420 &&
                    Math.Abs(bounds.Top - 72) < 1 && Math.Abs(bounds.Left + bounds.Width / 2 - 590) < 1,
                    "toast follows the prototype height, content width and top-center position: " + fixture.Scene);
                Check(System.Windows.Automation.AutomationProperties.GetName(card) == fixture.Message &&
                    !host.IsHitTestVisible && !host.Focusable && !Descendants<ButtonBase>(host).Any(),
                    "toast has readable UIA text and cannot steal pointer or keyboard input: " + fixture.Scene);
            }
            host.Items.Clear();
            foreach (var fixture in DesktopAcceptance.ToastFixtures) host.Show(fixture.Message, fixture.Level, TimeSpan.FromMinutes(1));
            Capture(root, output, "toast-stacked-minimum", 1000, 600);
            var cards = Descendants<Border>(host).Where(border => border.Name == "ToastCard").ToArray();
            var tops = cards.Select(card => card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize)).Top).ToArray();
            Check(host.Items.Select(item => item.Level).SequenceEqual(DesktopAcceptance.ToastFixtures.Select(item => item.Level)) &&
                tops.Zip(tops.Skip(1)).All(pair => Math.Abs(pair.Second - pair.First - 58) < 1),
                "stacked toasts preserve arrival order and 10 DIP gaps at the minimum window size");
            host.Items.Clear();
            host.Show(string.Concat(Enumerable.Repeat("这是一条用于检查长文案换行的提示。", 8)), ToastLevel.Error, TimeSpan.FromMinutes(1));
            Capture(root, output, "toast-long-message-minimum", 1000, 600);
            var longCard = Descendants<Border>(host).Single(border => border.Name == "ToastCard");
            Check(longCard.ActualWidth <= 420 && longCard.ActualHeight > 48 &&
                Descendants<TextBlock>(longCard).All(text => text.ActualWidth <= 356),
                "long toast wraps inside its 420 DIP maximum and grows vertically");
            host.Items.Clear();
            var now = DateTimeOffset.UtcNow;
            host.Show("first", ToastLevel.Success, now: now);
            host.Show("warning", ToastLevel.Warning, now: now);
            host.Show("fresh", ToastLevel.Success, now: now.AddSeconds(2));
            host.Expire(now.AddSeconds(3));
            Check(host.Items.Select(item => item.Message).SequenceEqual(new[] { "warning", "fresh" }),
                "a three-second expiry removes only the expired notice and preserves newer messages");
            host.Expire(now.AddSeconds(5));
            Check(host.Items.Count == 0 && ToastHost.Duration(ToastLevel.Error) == TimeSpan.FromSeconds(5),
                "success/info last three seconds and warning/error last five seconds");
            for (var index = 0; index < 5; index++) host.Show(index.ToString(), now: now);
            Check(host.Items.Select(item => item.Message).SequenceEqual(new[] { "1", "2", "3", "4" }),
                "a fifth toast evicts the oldest and retains at most four notices");
            host.Dispose();
            host.Show("after close");
            Check(host.Items.Count == 0 && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                "disposing the host stops feedback and the offscreen suite creates no native window");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyMainWindowSizing(string dataRoot, string output)
    {
        var startupDirectory = Path.Combine(dataRoot, "startup-window-sizing");
        var startupStore = new BlueLink.Appearance.WindowPreferencesStore(startupDirectory, "main");
        Check(startupStore.Save(new(1040, 630)), "persist a nondefault startup size");
        var restored = new MainWindow(initializeRuntime: false, dataRoot: startupDirectory);
        try
        {
            Check(restored.Width == 1040 && restored.Height == 630 &&
                new WindowInteropHelper(restored).Handle == IntPtr.Zero,
                "both saved dimensions are restored before native window creation");
        }
        finally { WaitForUiTask(restored.DisposeAsync().AsTask()); }
        var directory = Path.Combine(dataRoot, "window-sizing");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            Check(main.Width == 1180 && main.Height == 720 && main.MinWidth == 1000 && main.MinHeight == 600,
                "main window has compact defaults and explicit minimums");
            WaitForUiTask(main.ViewModel.InitializeLocalStateAsync());
            main.LoadVisualFixture();
            var root = DetachForRendering(main);
            foreach (var (width, height, name) in new[] { (1180, 720, "default"), (1000, 600, "minimum"), (1600, 1000, "large") })
            {
                main.ViewModel.ShowFiles = false;
                Capture(root, output, "main-chat-" + name, width, height);
                Check(((FrameworkElement)main.FindName("DevicesSidebar")).ActualWidth <= 340 &&
                    ((FrameworkElement)main.FindName("MessageComposer")).ActualHeight >= 104, "chat adapts without losing its composer: " + name);
                main.ViewModel.ShowFiles = true;
                Capture(root, output, "main-files-" + name, width, height);
                Check(((FrameworkElement)main.FindName("FileSearchInput")).ActualWidth >= 180, "file search remains usable: " + name);
                var count = Application.Current.Windows.Count;
                main.OpenSettings();
                Capture(root, output, "main-settings-" + name, width, height);
                var page = main.ActiveSettingsPage!;
                var save = (FrameworkElement)page.FindName("SaveButton");
                var bounds = save.TransformToAncestor(root).TransformBounds(new Rect(save.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= width && bounds.Top >= 0 && bounds.Bottom <= height,
                    "settings bottom actions fit the same main window: " + name);
                Check(Application.Current.Windows.Count == count && main.ViewModel.IsSettingsOpen &&
                    ((FrameworkElement)main.FindName("HomeWorkspace")).Visibility == Visibility.Collapsed,
                    "settings navigation never creates another window: " + name);
                main.ViewModel.ShowFiles = false;
                WaitForUiTask(main.ViewModel.SetWindowFocusAsync(true));
                Check(!main.ViewModel.IsConversationVisible("fixture-connected"), "covered conversation is not marked read: " + name);
                Click(page, "取消");
                Check(main.ActiveSettingsPage is null && !main.ViewModel.IsSettingsOpen &&
                    main.ViewModel.Messages.Count == 3, "returning from settings preserves the conversation: " + name);
            }
            var store = new BlueLink.Appearance.WindowPreferencesStore(directory, "main");
            Check(store.Save(new(1240, 760)), "save actual sizing fixture");
            main.WindowSizing.Restore(new Size(1920, 1040));
            main.OpenSettings();
            Check(main.Width == 1240 && main.Height == 760, "opening settings preserves restored main dimensions");
            Check(main.TryCloseSettings() && main.Width == 1240 && main.Height == 760, "returning preserves user dimensions");
            main.OpenSettings(connections: true);
            Check(((FrameworkElement)main.ActiveSettingsPage!.FindName("ConnectionPage")).Visibility == Visibility.Visible,
                "trust management routes to the inline connection page");
            Check(new WindowInteropHelper(main).Handle == IntPtr.Zero, "all main resize/navigation checks avoid a native window");
            var search = new MessageSearchWindow("QA 设备", main.ViewModel.Messages);
            try
            {
                var searchRoot = DetachForRendering(search);
                Capture(searchRoot, output, "search-minimum", 620, 480);
                Check(search.MinWidth == 620 && search.MinHeight == 480 &&
                    ((FrameworkElement)search.FindName("QueryInput")).ActualWidth >= 500,
                    "minimum search size retains its input and result area");
                Check(new WindowInteropHelper(search).Handle == IntPtr.Zero, "search layout creates no HWND");
            }
            finally { search.Close(); }
            var preview = new ImagePreviewWindow(Path.Combine(output, "main-chat-minimum.png"), "QA 布局图");
            try
            {
                var previewRoot = DetachForRendering(preview);
                foreach (var (width, height, name) in new[] { (720, 480, "minimum"), (1000, 700, "default") })
                {
                    Capture(previewRoot, output, "preview-" + name, width, height);
                    foreach (var button in Descendants<Button>(previewRoot).Where(value => value.ActualHeight > 0))
                    {
                        var bounds = button.TransformToAncestor(previewRoot).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Left >= 0 && bounds.Right <= width && bounds.Top >= 0 && bounds.Bottom <= height,
                            "preview action remains reachable at " + name + ": " + System.Windows.Automation.AutomationProperties.GetName(button));
                    }
                }
                preview.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Capture(previewRoot, output, "preview-fit-prototype", 1000, 700);
                var fit = (Button)preview.FindName("FitButton");
                var actual = (Button)preview.FindName("ActualSizeButton");
                var navigator = (Border)preview.FindName("Navigator");
                Check(fit.Tag is true && actual.Tag is false && navigator.Visibility == Visibility.Collapsed,
                    "the preview initially fits the image and highlights only the fit action");
                Check(fit.Background is SolidColorBrush fitBackground && fitBackground.Color == ((SolidColorBrush)Application.Current.FindResource("SoftBlueBrush")).Color,
                    "the active fit mode paints the prototype blue background instead of merely changing internal state");
                Check(((TextBlock)preview.FindName("TitleBarFileName")).Text == "QA 布局图",
                    "preview title preserves the attachment display name instead of replacing it with its storage filename");
                actual.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(actual.Tag is true && fit.Tag is false && ((TextBlock)preview.FindName("ZoomText")).Text == "100%",
                    "actual-size action uses a 100 percent scale and updates its selected state");
                var zoomIn = Descendants<Button>(previewRoot).Single(value => System.Windows.Automation.AutomationProperties.GetName(value) == "放大图片");
                for (var step = 0; step < 3; step++) zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Capture(previewRoot, output, "preview-zoomed-prototype", 1000, 700);
                Check(actual.Tag is false && fit.Tag is false && navigator.Visibility == Visibility.Visible,
                    "zooming removes fixed-mode highlights and displays the cropped-image navigator");
                var image = (System.Windows.Controls.Image)preview.FindName("PreviewImage");
                var imageBounds = (Border)preview.FindName("PreviewImageBounds");
                Check(ReferenceEquals(image.RenderTransform, imageBounds.RenderTransform) && image.Width == imageBounds.Width && image.Height == imageBounds.Height,
                    "the image outline follows the same image dimensions and transform");
                Descendants<Button>(previewRoot).Single(value => System.Windows.Automation.AutomationProperties.GetName(value) == "重置图片预览")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(fit.Tag is true && navigator.Visibility == Visibility.Collapsed, "reset restores fit mode and hides the cropped-image navigator");
                Check(new WindowInteropHelper(preview).Handle == IntPtr.Zero, "preview layout creates no HWND");
            }
            finally { preview.Close(); }
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

    private void VerifyUsbPages(MainViewModel model, SettingsPage settings, FrameworkElement content, string output)
    {
        settings.ShowConnections();
        var toggle = (Wpf.Ui.Controls.ToggleSwitch)settings.FindName("ConnectionUsbToggle");
        toggle.IsChecked = true;
        DrainDispatcher();
        Check(!model.Settings.UsbEnabled, "USB draft cannot open a device before saving");
        Check(settings.FindName("UsbPage") is null, "USB has no separate details page");
        WaitForUiTask(model.SaveSettingsAsync(model.Settings with { UsbEnabled = true }));
        WaitForUiTask(model.InitializeLocalStateAsync());
        Check(model.Settings.UsbEnabled, "USB setting persists without starting native detection");
        Capture(content, output, "usb-settings-compact", 1040, 680);
        Check(!Descendants<TextBlock>(content).Any(text => text.Text == "查看 USB 详情" || text.Text == "等待连接 USB 数据线"),
            "connection settings contain no device-specific USB status or details action");
        WaitForUiTask(model.SaveSettingsAsync(model.Settings with { UsbEnabled = false }));
        Check(PresentationSource.FromVisual(settings) is null, "USB settings acceptance creates no desktop window");
    }

    private void VerifyUpdateWindows(MainViewModel model, string dataRoot, string output)
    {
        var source = new UpdateTestSource();
        using var workflow = new BlueLink.Updates.UpdateWorkflow(new BlueLink.Updates.UpdateService(
            Path.Combine(dataRoot, "qa-updates"), source, new UpdateTestVerifier()));
        WaitForUiTask(workflow.CheckAsync());
        var window = new UpdateWindow(workflow, () => false);
        var content = DetachForRendering(window);
        Capture(content, output, "update-available", 540, 350);
        source.Mode = "hold";
        var download = workflow.DownloadAsync();
        WaitForUiTask(source.Waiting.Task);
        Capture(content, output, "update-downloading", 540, 350);
        Check(workflow.Stage == BlueLink.Updates.UpdateStage.Downloading && Math.Abs(workflow.Percent - 38) < 0.1 &&
            !((Button)window.FindName("UpdatePrimaryButton")).IsEnabled, "download progress and disabled action reflect the streamed bytes");
        var progress = (ProgressBar)window.FindName("DownloadProgressBar");
        Check(Descendants<FrameworkElement>(progress).Single(value => value.Name == "PART_Track").ActualHeight >= 4,
            "official download progress template keeps a visible four-DIP track");
        workflow.Cancel(); WaitForUiTask(download);
        source.Mode = "corrupt"; WaitForUiTask(workflow.DownloadAsync());
        Capture(content, output, "update-download-failed", 540, 350);
        source.Mode = "normal"; WaitForUiTask(workflow.DownloadAsync());
        Capture(content, output, "update-ready", 540, 350);
        Check(workflow.Stage == BlueLink.Updates.UpdateStage.Ready && File.Exists(workflow.Package!.Path), "Ready represents a complete fixture package");
        var action = (Button)window.FindName("UpdatePrimaryButton");
        var note = (Border)window.FindName("UpdateNote");
        Check(action.TranslatePoint(new Point(0, 0), content).Y >= note.TranslatePoint(new Point(0, note.ActualHeight), content).Y &&
              action.TranslatePoint(new Point(0, action.ActualHeight), content).Y <= 350,
            "update notes and actions remain separated inside the fixed dialog");
        WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "dark", Language = "en-US" }));
        workflow.RefreshText();
        window.Close();
        window = new UpdateWindow(workflow, () => false);
        content = DetachForRendering(window);
        Capture(content, output, "update-ready-dark-english", 540, 350);
        Check(!window.IsVisible && !window.IsActive && new WindowInteropHelper(window).Handle == IntPtr.Zero,
            "update verification sends no native window or installer launch");
        window.Close();
        WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = "light", Language = "zh-CN" }));
    }

    private static FrameworkElement DetachForRendering(Window window)
    {
        var root = (FrameworkElement)window.Content;
        if (root is Panel { Background: null } panel) panel.Background = window.Background;
        root.DataContext = window.DataContext;
        // Disconnected visuals need a resource owner to receive live theme dictionary changes.
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(window.Resources);
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        if (window is MainWindow) root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "InkBrush");
        if (NameScope.GetNameScope(window) is { } names) NameScope.SetNameScope(root, names);
        window.Content = null;
        return root;
    }

    private static FrameworkElement DetachForRendering(SettingsPage page) => page;

    private static void GoAbout(SettingsPage window) =>
        ((FrameworkElement)window.FindName("AboutNavigationItem")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Click(DependencyObject root, string label) =>
        Descendants<Button>(root).First(value => value.Content as string == label)
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private void Capture(FrameworkElement root, string output, string name, int width, int height)
    {
        Layout(root, width, height);
        if (Descendants<ListBox>(root).FirstOrDefault(value => value.Name == "TransferList" && Displayed(value, root)) is { } fileList)
        {
            // Recreated grouped containers receive Loaded in a desktop tree; emulate only that lifecycle here.
            foreach (var group in Descendants<GroupItem>(fileList)) group.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            foreach (var item in Descendants<ListBoxItem>(fileList)) item.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Layout(root, width, height);
        }
        VerifyContentGeometry(root, name);
        Check(Math.Abs(root.ActualWidth - width) < 1 && Math.Abs(root.ActualHeight - height) < 1,
            $"content arranged to requested bounds: {name}");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        DrainDispatcher();
        bitmap.Clear();
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
        _images.Add(new { name, width, height, dpi = 96 });
    }

    private static void Layout(FrameworkElement root, int width, int height)
    {
        DrainDispatcher();
        root.InvalidateMeasure();
        root.InvalidateArrange();
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        DrainDispatcher();
        foreach (var visual in Descendants<UIElement>(root)) visual.InvalidateVisual();
        root.InvalidateVisual();
        root.UpdateLayout();
        DrainDispatcher();
        // Detached content has no automatic Loaded event. WPF UI initializes caption ink there.
        // Pair the events so the temporary tree leaves no property-descriptor subscriptions behind.
        foreach (var button in Descendants<Wpf.Ui.Controls.TitleBarButton>(root))
        {
            button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                if (button.IsHovered) { button.RemoveHover(); button.Hover(); }
            }
            finally { button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }
        DrainDispatcher();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForUiTask(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException(label);
        _checks++;
        _passedChecks.Add(label);
    }

    private void VerifyContentGeometry(FrameworkElement root, string scene)
    {
        Check(PresentationSource.FromVisual(root) is null && Application.Current.Windows.Cast<Window>()
                .All(window => new WindowInteropHelper(window).Handle == IntPtr.Zero),
            "no native window or desktop input target: " + scene);
        Check(Descendants<Control>(root).Where(control => Displayed(control, root)).All(control => control.FocusVisualStyle is null),
            "visible controls have no additional focus adorner: " + scene);
        foreach (var button in Descendants<Wpf.Ui.Controls.TitleBarButton>(root).Where(button => Displayed(button, root)))
        {
            var expected = button.IsHovered ? button.MouseOverButtonsForeground ?? button.ButtonsForeground : button.ButtonsForeground;
            var glyph = (System.Windows.Shapes.Path)button.Template.FindName("CanvasPath", button);
            Check(glyph.Fill is SolidColorBrush actual && expected is SolidColorBrush ink && actual.Color == ink.Color,
                "caption glyph uses the current theme or hover ink: " + scene + "/" + button.ButtonType);
        }
        foreach (var scroll in Descendants<ScrollViewer>(root).Where(value => Displayed(value, root)))
        {
            var viewport = Descendants<ScrollContentPresenter>(scroll).FirstOrDefault();
            if (viewport is null || viewport.ActualWidth < 1 || viewport.ActualHeight < 1) continue;
            var label = string.IsNullOrEmpty(scroll.Name) ? scroll.GetType().Name : scroll.Name;
            foreach (var bar in Descendants<ScrollBar>(scroll).Where(value =>
                         ReferenceEquals(value.TemplatedParent, scroll) && Displayed(value, root)))
            {
                var end = viewport.TranslatePoint(new Point(viewport.ActualWidth, viewport.ActualHeight), scroll);
                var start = bar.TranslatePoint(new Point(), scroll);
                var gap = bar.Orientation == Orientation.Vertical ? start.X - end.X : start.Y - end.Y;
                AuditGeometry(gap >= -1, $"scrollbar outside content: {scene}/{label}/{bar.Orientation} (gap {gap:F2} DIP)");
            }
        }
        foreach (var input in Descendants<Wpf.Ui.Controls.TextBox>(root).Where(value => value.Name == "MessageInput" && Displayed(value, root)))
        {
            var content = (Border)input.Template.FindName("ContentBorder", input);
            var accent = (Border)input.Template.FindName("AccentBorder", input);
            AuditGeometry(content.BorderThickness == new Thickness(0) && content.Background is SolidColorBrush { Color.A: 0 } &&
                accent.BorderBrush is SolidColorBrush { Color.A: 0 },
                $"composer input has no rendered inner border, underline or background: {scene}/enabled={input.IsEnabled}/focused={input.IsFocused}");
        }
        foreach (var card in Descendants<Border>(root).Where(value => value.Name == "ConversationCard" && Displayed(value, root)))
        {
            var peer = (BlueLink.Domain.ConversationSummary)card.DataContext;
            var dot = Descendants<System.Windows.Shapes.Ellipse>(card).Single(value => value.Name == "StateDot");
            var badge = Descendants<Border>(card).Single(value => value.Name == "UnreadBadge");
            AuditGeometry(badge.Visibility == (peer.HasUnread ? Visibility.Visible : Visibility.Collapsed) &&
                dot.Visibility == (peer.HasUnread ? Visibility.Collapsed : Visibility.Visible),
                $"conversation badge and status dot are mutually exclusive: {scene}/{peer.PeerId}");
            FrameworkElement indicator = peer.HasUnread ? badge : dot;
            var bounds = indicator.TransformToAncestor(card).TransformBounds(new Rect(indicator.RenderSize));
            AuditGeometry(Displayed(indicator, root) && Math.Abs(bounds.Top + bounds.Height / 2 - card.ActualHeight / 2) <= 1 &&
                bounds.Right <= card.ActualWidth - card.Padding.Right + 1,
                $"conversation indicator is centered vertically inside the card: {scene}/{peer.PeerId}");
            if (peer.HasUnread)
            {
                var caption = Descendants<TextBlock>(badge).Single();
                var captionBounds = caption.TransformToAncestor(badge).TransformBounds(new Rect(caption.RenderSize));
                AuditGeometry(caption.Text == peer.UnreadText && captionBounds.Left >= 0 &&
                    captionBounds.Right <= badge.ActualWidth - badge.Padding.Right + 1,
                    $"conversation badge count fits including 99+: {scene}/{peer.PeerId}");
            }
            else
            {
                var resource = peer.Availability == BlueLink.Domain.DeviceAvailability.Connected ? "SuccessBrush" :
                    peer.Availability == BlueLink.Domain.DeviceAvailability.Connectable ? "BlueBrush" : "PanelAEB7C6Brush";
                AuditGeometry(((SolidColorBrush)dot.Fill).Color == ((SolidColorBrush)card.FindResource(resource)).Color,
                    $"conversation without unread messages restores its status color: {scene}/{peer.PeerId}");
            }
        }
        foreach (var header in Descendants<Grid>(root).Where(value => value.Name == "NearbyGroupHeader" && Displayed(value, root)))
        {
            var title = (Button)header.Children[0];
            var scan = (StackPanel)header.Children[1];
            var toggle = (Button)header.Children[2];
            var titleBounds = title.TransformToAncestor(header).TransformBounds(new Rect(title.RenderSize));
            var toggleBounds = toggle.TransformToAncestor(header).TransformBounds(new Rect(toggle.RenderSize));
            AuditGeometry(title.ActualWidth > 0 && titleBounds.Left >= -1 &&
                Math.Abs(toggleBounds.Right - header.ActualWidth) <= 1,
                "nearby title and chevron stay inside the header with the chevron at the right edge: " + scene);
            if (Displayed(scan, root))
            {
                var scanBounds = scan.TransformToAncestor(header).TransformBounds(new Rect(scan.RenderSize));
                AuditGeometry(titleBounds.Right <= scanBounds.Left - 7 &&
                    Math.Abs(toggleBounds.Left - scanBounds.Right - 8) <= 1 &&
                    Math.Abs(scanBounds.Top + scanBounds.Height / 2 - toggleBounds.Top - toggleBounds.Height / 2) <= 1,
                    "nearby scan status and toggle stay separated at the right: " + scene);
            }
            else
                AuditGeometry(Math.Abs(titleBounds.Right - toggleBounds.Left) <= 1,
                    "nearby idle header has no empty scan column: " + scene);
        }
        foreach (var button in Descendants<Button>(root).Where(value =>
                     value.Appearance == Wpf.Ui.Controls.ControlAppearance.Transparent && Displayed(value, root)))
        {
            var inset = button.Template.FindName("InsetBorder", button) as Border;
            var outer = button.Template.FindName("ContentBorder", button) as Border;
            AuditGeometry(button.BorderThickness == new Thickness(0) &&
                  (inset is null || inset.BorderThickness == new Thickness(0)) &&
                  (outer is null || outer.BorderThickness == new Thickness(0)),
                $"transparent action has no rendered border: {scene}/{button.Name}/{button.Content}");
        }
    }

    private static bool Displayed(FrameworkElement element, FrameworkElement root)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private void AuditGeometry(bool passed, string label)
    {
        if (passed) Check(true, label);
        else _geometryFailures.Add(label);
    }

}
