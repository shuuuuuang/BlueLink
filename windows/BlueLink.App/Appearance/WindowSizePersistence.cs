using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BlueLink.Appearance;

/// <summary>Production tracking starts only after the real window loads; no HWND is created here.</summary>
internal sealed class WindowSizePersistence
{
    private readonly Window _window;
    private readonly string _key;
    private readonly string? _dataDirectory;
    private readonly WindowSizePolicy _policy;
    private readonly DispatcherTimer _saveTimer;
    private WindowPreferencesStore? _store;
    private WindowSizePreference _latest;
    private bool _tracking, _adjusting, _closed, _suspended;
    private HwndSource? _source;

    public WindowSizePersistence(Window window, string key, string? dataDirectory = null)
    {
        _window = window; _key = key; _dataDirectory = dataDirectory; _policy = WindowSizePolicy.For(key);
        _latest = new(_policy.DefaultWidth, _policy.DefaultHeight);
        window.Width = _policy.DefaultWidth; window.Height = _policy.DefaultHeight;
        window.MinWidth = _policy.MinWidth; window.MinHeight = _policy.MinHeight;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += SaveTimer_Tick;
        window.SourceInitialized += SourceInitialized;
        window.Loaded += Loaded;
        window.SizeChanged += SizeChanged;
        window.StateChanged += StateChanged;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closing += Closing;
        window.Closed += Closed;
        window.DpiChanged += DpiChanged;
        // Apply both dimensions before native creation. Resizing during
        // SourceInitialized can reenter layout and overwrite the second dimension.
        var initial = Restore(SystemParameters.WorkArea.Size);
        if (initial.Maximized) window.WindowState = WindowState.Maximized;
    }

    private WindowPreferencesStore Store => _store ??= new WindowPreferencesStore(_dataDirectory ??
        (_window.Owner as MainWindow)?.ViewModel.DataDirectory ??
        (Application.Current?.MainWindow as MainWindow)?.ViewModel.DataDirectory ??
        BlueLink.Storage.AppStoragePaths.DatabaseDirectory, _key);

    internal WindowSizePreference Restore(Size workArea)
    {
        var value = _policy.Resolve(Store.Read(), workArea.Width, workArea.Height);
        _window.Width = value.Width; _window.Height = value.Height;
        _latest = value;
        return value;
    }

    private void SourceInitialized(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowHook);
    }

    private Size CurrentWorkArea()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea.Size;
        var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(_window);
        return new(bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        _tracking = true;
        QueueWorkAreaAdjustment();
    }
    private void SizeChanged(object sender, SizeChangedEventArgs args) => ScheduleSave();
    private void StateChanged(object? sender, EventArgs args) => ScheduleSave();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!(bool)args.NewValue) Flush(); }
    private void Closing(object? sender, CancelEventArgs args) => Flush();
    private void DpiChanged(object sender, DpiChangedEventArgs args) => QueueWorkAreaAdjustment();
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) QueueWorkAreaAdjustment(); // WM_DISPLAYCHANGE
        return IntPtr.Zero;
    }
    private void QueueWorkAreaAdjustment() => _window.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed || _suspended || !_tracking || _window.WindowState != WindowState.Normal) return;
        _adjusting = true;
        try
        {
            var work = CurrentWorkArea();
            var value = _policy.Resolve(new(_window.Width, _window.Height), work.Width, work.Height);
            _window.Width = value.Width; _window.Height = value.Height;
        }
        finally { _adjusting = false; }
        ScheduleSave();
    }));

    internal void Suspend()
    {
        Flush();
        _suspended = true;
        _saveTimer.Stop();
    }

    internal void Resume()
    {
        _suspended = false;
        ScheduleSave();
    }

    private void Capture()
    {
        var bounds = _window.RestoreBounds;
        _latest = WindowSizePolicy.Capture(_latest, (int)_window.WindowState,
            _window.ActualWidth, _window.ActualHeight, bounds.Width, bounds.Height);
    }
    private void ScheduleSave()
    {
        if (!_tracking || _adjusting || _closed || _suspended) return;
        Capture(); _saveTimer.Stop(); _saveTimer.Start();
    }
    private async void SaveTimer_Tick(object? sender, EventArgs args)
    {
        _saveTimer.Stop();
        if (!_closed && !_suspended) { Capture(); await Store.SaveAsync(_latest); }
    }
    private void Flush()
    {
        if (!_tracking || _closed || _suspended) return;
        _saveTimer.Stop(); Capture(); Store.Save(_latest);
    }
    private void Closed(object? sender, EventArgs args)
    {
        Flush(); _closed = true; _saveTimer.Stop(); _saveTimer.Tick -= SaveTimer_Tick;
        _source?.RemoveHook(WindowHook);
        _window.SourceInitialized -= SourceInitialized; _window.Loaded -= Loaded;
        _window.SizeChanged -= SizeChanged; _window.StateChanged -= StateChanged;
        _window.IsVisibleChanged -= VisibilityChanged; _window.Closing -= Closing;
        _window.Closed -= Closed; _window.DpiChanged -= DpiChanged;
    }
}
