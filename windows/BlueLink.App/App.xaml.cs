using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink.Shared;
using BlueLink.Security;
using BlueLink.Storage;
using Forms = System.Windows.Forms;

namespace BlueLink;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _exitSignal;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _exitRegistration;
    private RegisteredWaitHandle? _showRegistration;
    private string? _startupSmokeDataRoot;
    private int _exitStarted;
    private string _installRoot = string.Empty;
    internal bool ExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        const string uiSmokePrefix = "--ui-smoke-test=";
        const string previewSmokePrefix = "--preview-ui-smoke-test=";
        const string previewSourcePrefix = "--preview-source=";
        var uiSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(uiSmokePrefix, StringComparison.OrdinalIgnoreCase))?[uiSmokePrefix.Length..];
        var previewSmokePath = e.Args.FirstOrDefault(value =>
            value.StartsWith(previewSmokePrefix, StringComparison.OrdinalIgnoreCase))?[previewSmokePrefix.Length..];
        var previewSource = e.Args.FirstOrDefault(value =>
            value.StartsWith(previewSourcePrefix, StringComparison.OrdinalIgnoreCase))?[previewSourcePrefix.Length..];
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
        var startupSmokeTest = controlChannelSmoke || acceptanceBackground || uiSmokePath is not null || previewSmokePath is not null || e.Args.Any(value =>
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
        var mutexName = startupSmokeTest
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
                $"BlueLinkStartupSmoke-{Environment.ProcessId}");
            Directory.CreateDirectory(_startupSmokeDataRoot);
        }
        var window = new MainWindow(initializeRuntime: !startupSmokeTest,
            dataRoot: _startupSmokeDataRoot);
        if (uiSmokeCompact) { window.Width = 1180; window.Height = 720; }
        MainWindow = window;
        if (!startupSmokeTest || controlChannelSmoke)
        {
            InitializeControlChannel();
        }
        if (!startupSmokeTest)
        {
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
        }
        else
        {
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            if (previewSmokePath is not null)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => CapturePreviewSnapshot(window, previewSource, previewSmokePath)));
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

    private void CapturePreviewSnapshot(Window owner, string? sourcePath, string outputPath)
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
                try { SaveWindowSnapshot(preview, outputPath); }
                catch (Exception failure) { WriteCrashLog(failure); Environment.ExitCode = 4; }
                preview.Close();
                RequestExit();
            }));
        preview.Show();
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
        if (_startupSmokeDataRoot is not null)
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
            var directory = Path.Combine(
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
