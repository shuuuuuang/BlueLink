using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink.Shared;
using BlueLink.Security;
using BlueLink.Storage;
using Forms = System.Windows.Forms;

namespace BlueLink;

public partial class App : System.Windows.Application
{
    static App() => Appearance.ControlInteractionPolicy.Initialize();

    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private Notifications.TrayNotificationService? _notifications;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _exitSignal;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _exitRegistration;
    private RegisteredWaitHandle? _showRegistration;
    private string? _startupSmokeDataRoot;
    private int _exitStarted;
    private string _installRoot = string.Empty;
    private bool _resourceOnly;
    private bool _keepDesktopAcceptanceData;
    internal bool ExitRequested { get; private set; }

    public static App CreateResourceOnlyHost()
    {
        var app = new App { _resourceOnly = true, ExitRequested = true };
        app.InitializeComponent();
        return app;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (_resourceOnly) return;
        base.OnStartup(e);
        const string uiSmokePrefix = "--ui-smoke-test=";
        const string homeUiPrefix = "--home-ui-test=";
        const string desktopUiPrefix = "--desktop-ui-test=";
        var desktopUiName = e.Args.FirstOrDefault(value => value.StartsWith(desktopUiPrefix, StringComparison.OrdinalIgnoreCase))?[desktopUiPrefix.Length..];
        if (desktopUiName is not null && !System.Text.RegularExpressions.Regex.IsMatch(desktopUiName, "^[a-zA-Z0-9_-]{1,48}$"))
            throw new ArgumentException("Desktop UI test name must contain only letters, digits, underscores or hyphens.");
        _keepDesktopAcceptanceData = desktopUiName is not null;
        const string desktopScenePrefix = "--desktop-ui-scene=";
        var desktopScene = e.Args.FirstOrDefault(value => value.StartsWith(desktopScenePrefix, StringComparison.OrdinalIgnoreCase))?[desktopScenePrefix.Length..];
        if (desktopScene is not null && (desktopUiName is null || !DesktopAcceptance.Scenes.Contains(desktopScene)))
            throw new ArgumentException("A supported desktop scene requires an isolated desktop UI test name.");
        var homeUiName = e.Args.FirstOrDefault(value => value.StartsWith(homeUiPrefix, StringComparison.OrdinalIgnoreCase))?[homeUiPrefix.Length..];
        if (homeUiName is not null && !System.Text.RegularExpressions.Regex.IsMatch(homeUiName, "^[a-zA-Z0-9_-]{1,48}$"))
            throw new ArgumentException("Home UI test name must contain only letters, digits, underscores or hyphens.");
        const string previewSmokePrefix = "--preview-ui-smoke-test=";
        const string previewSourcePrefix = "--preview-source=";
        const string previewInteractionArgument = "--preview-interaction-smoke-test";
        const string controlTemplateProbePrefix = "--control-template-smoke-test=";
        const string dialogSmokePrefix = "--dialog-ui-smoke-test=";
        const string trustDialogSmokePrefix = "--trust-dialog-ui-smoke-test=";
        var uiSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(uiSmokePrefix, StringComparison.OrdinalIgnoreCase))?[uiSmokePrefix.Length..];
        var previewSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(previewSmokePrefix, StringComparison.OrdinalIgnoreCase))?[previewSmokePrefix.Length..];
        var previewSource = e.Args.FirstOrDefault(value =>
            value.StartsWith(previewSourcePrefix, StringComparison.OrdinalIgnoreCase))?[previewSourcePrefix.Length..];
        var previewZoomed = e.Args.Any(value =>
            string.Equals(value, "--preview-zoomed", StringComparison.OrdinalIgnoreCase));
        var previewInteraction = e.Args.Any(value =>
            string.Equals(value, previewInteractionArgument, StringComparison.OrdinalIgnoreCase));
        var controlTemplateProbePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(controlTemplateProbePrefix, StringComparison.OrdinalIgnoreCase))?
            [controlTemplateProbePrefix.Length..];
        var dialogSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(dialogSmokePrefix, StringComparison.OrdinalIgnoreCase))?
            [dialogSmokePrefix.Length..];
        var trustDialogSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(trustDialogSmokePrefix, StringComparison.OrdinalIgnoreCase))?
            [trustDialogSmokePrefix.Length..];
        var uiSmokeCollapsed = e.Args.Any(value =>
            string.Equals(value, "--ui-collapsed", StringComparison.OrdinalIgnoreCase));
        var uiSmokeCompact = e.Args.Any(value =>
            string.Equals(value, "--ui-compact", StringComparison.OrdinalIgnoreCase));
        var acceptanceBackground = e.Args.Any(value =>
            string.Equals(value, "--acceptance-background", StringComparison.OrdinalIgnoreCase));
        var controlChannelSmoke = e.Args.Any(value =>
            string.Equals(value, "--control-channel-smoke-test", StringComparison.OrdinalIgnoreCase));
        var backgroundLaunch = e.Args.Any(value =>
            string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase)) || acceptanceBackground || controlChannelSmoke;
        var startupSmokeTest = desktopUiName is not null || homeUiName is not null || controlChannelSmoke || acceptanceBackground || uiSmokePath is not null ||
            previewSmokePath is not null || previewInteraction ||
            controlTemplateProbePath is not null || dialogSmokePath is not null ||
            trustDialogSmokePath is not null ||
            e.Args.Any(value =>
            string.Equals(value, "--startup-smoke-test", StringComparison.OrdinalIgnoreCase));
        _installRoot = WindowsAppControlChannel.CurrentInstallRoot();
        if (TryRunMaintenance(e.Args))
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ??
                new InvalidOperationException($"Unhandled runtime value: {args.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        // Visual/startup smoke tests deliberately run beside an installed
        // background instance and use an isolated data root.  They must not
        // be rejected by the production single-instance gate.
        var mutexName = desktopUiName is not null ? $@"Local\BlueLink.Desktop.Acceptance.{desktopUiName}" : homeUiName is not null
            ? $@"Local\BlueLink.Desktop.HomeUI.{homeUiName}"
            : startupSmokeTest
            ? $@"Local\BlueLink.Desktop.Smoke.{Environment.ProcessId}"
            : WindowsAppControlChannel.InstanceMutexName(_installRoot);
        _instanceMutex = new Mutex(true, mutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            if (startupSmokeTest)
            {
                Environment.ExitCode = 3;
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }
            WindowsAppControlChannel.SignalShow(_installRoot);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        if (startupSmokeTest)
        {
            _startupSmokeDataRoot = Path.Combine(Path.GetTempPath(),
                desktopUiName is not null ? $"BlueLinkDesktopUI-{desktopUiName}" : homeUiName is null ? $"BlueLinkStartupSmoke-{Environment.ProcessId}" : $"BlueLinkHomeUI-{homeUiName}");
            Directory.CreateDirectory(_startupSmokeDataRoot);
        }
        if (_startupSmokeDataRoot is not null)
            BlueLink.Session.SessionLog.DirectoryPath = Path.Combine(_startupSmokeDataRoot, "Logs");
        var window = new MainWindow(initializeRuntime: !startupSmokeTest || homeUiName is not null,
            dataRoot: _startupSmokeDataRoot);
        if (uiSmokeCompact) { window.Width = 1180; window.Height = 720; }
        MainWindow = window;
        if (desktopUiName is not null)
        {
            window.Title = $"蓝联 / BlueLink · QA {desktopUiName}";
            window.Loaded += async (_, _) => await DesktopAcceptance.InitializeAsync(window, _startupSmokeDataRoot!, desktopScene);
            DesktopAcceptance.ObserveGeometry(window, _startupSmokeDataRoot!);
        }
        if (homeUiName is not null)
        {
            ExitRequested = true;
            window.ContentRendered += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var controls = new[] { "HomeTitleBar", "DevicesSidebar", "DeviceSearchInput", "HomeWorkspace" }
                    .Select(name => window.FindName(name) as FrameworkElement)
                    .Where(element => element is not null)
                    .Select(element => new { element!.Name, element.ActualWidth, element.ActualHeight });
                File.WriteAllText(Path.Combine(_startupSmokeDataRoot!, "home-geometry.json"),
                    JsonSerializer.Serialize(new { dpi.DpiScaleX, dpi.DpiScaleY, window.ActualWidth, window.ActualHeight, controls },
                        new JsonSerializerOptions { WriteIndented = true }));
            };
            window.Closed += (_, _) => RequestExit();
        }
        if (!startupSmokeTest || controlChannelSmoke)
        {
            InitializeControlChannel();
        }
        if (!startupSmokeTest || desktopUiName is not null)
        {
            Appearance.AppearanceService.StartWatching();
            _trayIcon = LoadProductIcon();
            _tray = new Forms.NotifyIcon
            {
                Text = "蓝联 · Bluetooth only",
                Icon = _trayIcon ?? SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = new Forms.ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("打开蓝联", null, (_, _) => ShowMainWindow());
            _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => RequestExit());
            _tray.DoubleClick += (_, _) => ShowMainWindow();
            _notifications = new Notifications.TrayNotificationService(_tray, window);
        }
        else if (homeUiName is null && desktopUiName is null)
        {
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            if (controlTemplateProbePath is not null)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => CaptureControlTemplateProbe(window, controlTemplateProbePath)));
            }
            else if (dialogSmokePath is not null)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => CaptureDialogSmoke(dialogSmokePath)));
            }
            else if (trustDialogSmokePath is not null)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => CaptureTrustDialogSmoke(trustDialogSmokePath)));
            }
            else if (previewInteraction)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => ShowPreviewInteraction(previewSource)));
            }
            else if (previewSmokePath is not null)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => CapturePreviewSnapshot(window, previewSource, previewSmokePath, previewZoomed)));
            }
            else if (uiSmokePath is not null)
            {
                window.LoadVisualFixture(expanded: !uiSmokeCollapsed);
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        try { SaveWindowSnapshot(window, uiSmokePath); }
                        catch (Exception failure) { WriteCrashLog(failure); Environment.ExitCode = 4; }
                        RequestExit();
                    }));
            }
            else if (!acceptanceBackground && !controlChannelSmoke)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(RequestExit));
            }
        }
        window.Show();
        if (backgroundLaunch)
        {
            window.Hide();
            window.ShowInTaskbar = true;
        }
    }

    private void ShowPreviewInteraction(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            WriteCrashLog(new FileNotFoundException("Preview interaction source was not found.", sourcePath));
            Environment.ExitCode = 4;
            RequestExit();
            return;
        }

        var preview = new ImagePreviewWindow(sourcePath, Path.GetFileName(sourcePath))
        {
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        preview.Closed += (_, _) => RequestExit();
        preview.Show();
        preview.Activate();
    }

    private void CapturePreviewSnapshot(Window owner, string? sourcePath, string outputPath, bool zoomed)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            WriteCrashLog(new FileNotFoundException("Preview smoke-test source was not found.", sourcePath));
            Environment.ExitCode = 4;
            RequestExit();
            return;
        }

        var preview = new ImagePreviewWindow(sourcePath, Path.GetFileName(sourcePath))
        {
            Owner = owner,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000
        };
        preview.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                try
                {
                    if (zoomed) preview.LoadZoomedVisualFixture();
                    SaveWindowSnapshot(preview, outputPath);
                }
                catch (Exception failure) { WriteCrashLog(failure); Environment.ExitCode = 4; }
                preview.Close();
                RequestExit();
            }));
        preview.Show();
    }

    private void CaptureDialogSmoke(string outputPath)
    {
        try
        {
            var confirmed = BlueLinkDialog.Confirm(
                null,
                "对话框运行验证",
                "此窗口必须包含真实可见的官方 WPF UI 主按钮和取消按钮。",
                BlueLinkDialogTone.Information);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Confirmed = confirmed,
                OfficialFluentWindow = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!confirmed) Environment.ExitCode = 5;
        }
        catch (Exception failure)
        {
            WriteCrashLog(failure);
            Environment.ExitCode = 4;
        }
        RequestExit();
    }

    private void CaptureTrustDialogSmoke(string outputPath)
    {
        try
        {
            var request = new BlueLink.Security.TrustRequest(
                "附近设备",
                "482 719",
                "8A2C156482A69173D6071833E6AF21B6",
                "CC7D4E2F18A94355A0F6B1D2678C3E91");
            // This explicit UI smoke fixture has no transport; only the fixture simulates peer confirmation.
            request.PropertyChanged += (_, _) =>
            {
                if (request.Stage == BlueLink.Security.TrustStage.Waiting) request.Finish(BlueLink.Security.TrustStage.Completed);
            };
            new TrustConfirmationWindow(request).ShowDialog();
            var confirmed = request.Stage == BlueLink.Security.TrustStage.Completed;
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Confirmed = confirmed,
                OfficialFluentWindow = true,
                TrustConfirmation = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!confirmed) Environment.ExitCode = 5;
        }
        catch (Exception failure)
        {
            WriteCrashLog(failure);
            Environment.ExitCode = 4;
        }
        RequestExit();
    }

    private void CaptureControlTemplateProbe(Window owner, string outputPath)
    {
        var host = new Window
        {
            Width = 520,
            Height = 900,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/SettingsWindow.xaml", UriKind.Relative),
        });
        Style? ResolveStyle(object key) =>
            host.TryFindResource(key) as Style ?? TryFindResource(key) as Style;
        var panel = new StackPanel();
        host.Content = panel;
        var contextMenu = new ContextMenu();
        var menuItem = new MenuItem { Header = "菜单项" };
        contextMenu.Items.Add(menuItem);
        var controls = new List<(string Name, Control Control, object? StyleKey)>
        {
            ("Button", new Button { Content = "按钮" }, typeof(Button)),
            ("WpfUiButton", new Wpf.Ui.Controls.Button { Content = "WPF UI 按钮" },
                "SettingsButtonStyle"),
            ("TextBox", new TextBox { Text = "输入框" }, typeof(TextBox)),
            ("WpfUiTextBox", new Wpf.Ui.Controls.TextBox { Text = "WPF UI 输入框" },
                "SettingsTextBoxStyle"),
            ("ComboBox", new ComboBox { ItemsSource = new[] { "选项一", "选项二" }, SelectedIndex = 0 },
                typeof(ComboBox)),
            ("ComboBoxItem", new ComboBoxItem { Content = "下拉项" }, "BlueLinkComboBoxItemStyle"),
            ("CheckBox", new CheckBox { Content = "复选框" }, typeof(CheckBox)),
            ("RadioButton", new RadioButton { Content = "单选框" }, typeof(RadioButton)),
            ("TabControl", new TabControl(), typeof(TabControl)),
            ("TabItem", new TabItem { Header = "标签" }, typeof(TabItem)),
            ("ListBoxItem", new ListBoxItem { Content = "列表项" }, "TransparentListItemStyle"),
            ("ContextMenu", contextMenu, "BlueLinkContextMenuStyle"),
            ("MenuItem", menuItem, "BlueLinkMenuItemStyle"),
            ("ProgressBar", new ProgressBar { Value = 50 }, typeof(ProgressBar)),
            ("ScrollBar", new ScrollBar { Orientation = Orientation.Vertical, Height = 120 },
                typeof(ScrollBar)),
            ("Slider", new Slider { Value = 50, Width = 160 }, typeof(Slider)),
            ("TitleBar", new Wpf.Ui.Controls.TitleBar { Title = "标题栏" },
                typeof(Wpf.Ui.Controls.TitleBar)),
            ("ToggleSwitch", new Wpf.Ui.Controls.ToggleSwitch { Content = "开关" },
                "SettingsToggleStyle"),
        };
        foreach (var entry in controls)
        {
            if (entry.StyleKey is not null && ResolveStyle(entry.StyleKey) is Style style)
                entry.Control.Style = style;
            if (entry.Control is not ContextMenu && entry.Control is not MenuItem)
                panel.Children.Add(entry.Control);
        }

        var confirmation = BlueLinkDialog.CreateWindow("确认", "共用弹窗探针", BlueLinkDialogTone.Warning, true);
        confirmation.FocusVisualStyle = null;
        controls.Add(("ConfirmationWindow", confirmation, typeof(Wpf.Ui.Controls.FluentWindow)));

        try
        {
            host.Owner = owner;
            host.Show();
            host.UpdateLayout();
            contextMenu.PlacementTarget = host;
            contextMenu.IsOpen = true;
            contextMenu.UpdateLayout();

            var report = new List<object>();
            foreach (var entry in controls)
            {
                if (entry.Control.Style is null && entry.StyleKey is not null &&
                    ResolveStyle(entry.StyleKey) is Style style)
                    entry.Control.Style = style;
                if (entry.Control is not Window)
                {
                    entry.Control.Measure(new System.Windows.Size(360, 90));
                    entry.Control.Arrange(new Rect(0, 0,
                        Math.Max(1, entry.Control.DesiredSize.Width),
                        Math.Max(1, entry.Control.DesiredSize.Height)));
                    entry.Control.UpdateLayout();
                }
                var applied = entry.Control.ApplyTemplate();
                entry.Control.UpdateLayout();
                var visualChildren = VisualTreeHelper.GetChildrenCount(entry.Control);
                report.Add(new
                {
                    entry.Name,
                    StyleResolved = entry.Control.Style is not null,
                    TemplateResolved = entry.Control.Template is not null,
                    TemplateApplied = applied || visualChildren > 0,
                    VisualChildren = visualChildren,
                    FocusVisualSuppressed = entry.Control.FocusVisualStyle is null,
                });
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(
                new { controls = report },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception failure)
        {
            WriteCrashLog(failure);
            Environment.ExitCode = 4;
        }
        finally
        {
            contextMenu.IsOpen = false;
            host.Close();
            RequestExit();
        }
    }

    private static void SaveWindowSnapshot(Window window, string path)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement content)
            throw new InvalidOperationException("Window content is not renderable.");
        content.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? System.Windows.Media.Brushes.White, null,
                new Rect(0, 0, width, height));
            context.DrawRectangle(new System.Windows.Media.VisualBrush(content), null,
                new Rect(0, 0, width, height));
        }
        bitmap.Render(drawing);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static bool TryRunMaintenance(IReadOnlyList<string> args)
    {
        const string prefix = "--cleanup=";
        var argument = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null) return false;

        try
        {
            var requested = argument[prefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identity = new IdentityStore();
            var database = new BlueLinkDatabase();
            database.InitializeAsync(identity).GetAwaiter().GetResult();
            if (requested.Contains("chat")) database.ClearMessagesAsync().GetAwaiter().GetResult();
            if (requested.Contains("transfers")) database.ClearTransfersAsync().GetAwaiter().GetResult();
            if (requested.Contains("trust-settings"))
            {
                database.ResetTrustAndSettingsAsync().GetAwaiter().GetResult();
                identity.ClearTrustedIdentities();
            }
            Environment.ExitCode = 0;
        }
        catch (Exception failure)
        {
            WriteCrashLog(failure);
            Environment.ExitCode = 2;
        }
        return true;
    }

    internal void ShowMainWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            return;
        }
        MainWindow?.Show();
        if (MainWindow is { WindowState: WindowState.Minimized } value) value.WindowState = WindowState.Normal;
        MainWindow?.Activate();
    }

    internal void RequestExit()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RequestExit));
            return;
        }

        _ = RequestExitAsync();
    }

    internal async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        ExitRequested = true;
        try
        {
            if (_tray is not null)
            {
                _notifications?.Dispose(); _notifications = null;
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            if (MainWindow is MainWindow window)
                await window.DisposeAsync();
        }
        catch (Exception failure)
        {
            WriteCrashLog(failure);
        }
        finally
        {
            Shutdown();
        }
    }

    private void InitializeControlChannel()
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
            WindowsAppControlChannel.ExitEventName(_installRoot));
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
            WindowsAppControlChannel.ShowEventName(_installRoot));
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitSignal,
            (_, _) => RequestExit(), null, Timeout.Infinite, executeOnlyOnce: false);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => ShowMainWindow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Appearance.AppearanceService.StopWatching();
        _notifications?.Dispose(); _notifications = null;
        _exitRegistration?.Unregister(null);
        _showRegistration?.Unregister(null);
        _exitRegistration = null;
        _showRegistration = null;
        _exitSignal?.Dispose();
        _showSignal?.Dispose();
        _exitSignal = null;
        _showSignal = null;
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        if (_startupSmokeDataRoot is not null && !_keepDesktopAcceptanceData)
        {
            try { Directory.Delete(_startupSmokeDataRoot, recursive: true); }
            catch { }
            _startupSmokeDataRoot = null;
        }
        base.OnExit(e);
    }

    private static Icon? LoadProductIcon()
    {
        try
        {
            var resource = GetResourceStream(
                new Uri("pack://application:,,,/Assets/bluelink.ico", UriKind.Absolute));
            if (resource is null) return null;
            using (resource.Stream)
            using (var icon = new Icon(resource.Stream))
                return (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Persist the first failure before WPF terminates. Arbitrary dispatcher
        // exceptions are not swallowed because continuing with corrupted UI
        // state is less safe than a controlled restart.
        WriteCrashLog(e.Exception);
    }

    private static void WriteCrashLog(Exception failure)
    {
        try
        {
            var directory = Application.Current is App { _startupSmokeDataRoot: { } testRoot }
                ? Path.Combine(testRoot, "Logs")
                : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueLink", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "windows-crash.log"),
                $"[{DateTimeOffset.Now:O}] {failure}\n\n");
        }
        catch
        {
            // Crash logging must not replace the original exception.
        }
    }
}
