using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;

// Native popup regression on a private, never activated desktop. No SwitchDesktop,
// SendInput or cursor movement: messages are sent only to this test's own HWNDs.
internal static class NativeDatePopupVerification
{
    internal static void Run(string directory, bool privateDesktopChild = false)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (!privateDesktopChild) { LaunchPrivateDesktop(directory); return; }
        var privateName = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        Require(privateName.StartsWith("BlueLink-Date-", StringComparison.Ordinal) && privateName != InputDesktopName(),
            "native test child must run on its private non-input desktop");
        var log = new List<string>();
        Exception? failure = null;
        var foreground = GetForegroundWindow();
        var thread = new Thread(() =>
        {
            Application? app = null;
            Window? owner = null;
            Popup? parent = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var target = new Wpf.Ui.Controls.Button { Content = "日期范围", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                owner = new Window { Width = 1000, Height = 800, Content = target, ShowActivated = false, ShowInTaskbar = false };
                owner.Show(); Drain();
                var main = new MainWindow(false, Path.Combine(directory, "isolated-data"));
                try
                {
                    var initialize = main.ViewModel.InitializeLocalStateAsync();
                    while (!initialize.IsCompleted) Drain();
                    initialize.GetAwaiter().GetResult();
                    for (var cycle = 0; cycle < 10; cycle++)
                    {
                        var editor = main.CreateFileColumnEditor("Date");
                        parent = OpenEditor(editor, target);
                        Require(!editor.Dates.IsActive, "reset/reopen starts without a date filter");
                        for (var endpoint = 0; endpoint < 2; endpoint++)
                        {
                            var picker = endpoint == 0 ? editor.StartDate : editor.EndDate;
                            var selected = DateTime.Today.AddDays(-5 + endpoint);
                            Invoke(picker.Trigger);
                            Require(parent.IsOpen && picker.CalendarPopup.IsOpen, "calendar opens inside parent");
                            var day = Children<CalendarDayButton>(picker.Calendar).Single(b => b.DataContext is DateTime date && date == selected);
                            if (cycle == 0 && endpoint == 0)
                            {
                                SendMouse(day, 0x0200, 0);
                                SendMouse(day, 0x0201, 1); Drain();
                            }
                            else
                            {
                                // Routed pointer edges plus actual UIA day selection avoid requiring
                                // the private desktop to become the OS input desktop.
                                day.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                                var peer = new CalendarAutomationPeer(picker.Calendar).GetChildren().OfType<DateTimeAutomationPeer>()
                                    .Single(p => p.GetName() == selected.ToString("D", picker.Calendar.Language.GetSpecificCulture()));
                                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select(); Drain();
                            }
                            log.Add($"cycle={cycle}, endpoint={endpoint}, pressed: parent={parent.IsOpen}, calendar={picker.CalendarPopup.IsOpen}, date={picker.SelectedDate:yyyy-MM-dd}");
                            Require(picker.SelectedDate == selected && parent.IsOpen && picker.CalendarPopup.IsOpen, "pointer-down selects date without destroying the calendar before release");
                            if (cycle == 0 && endpoint == 0) SendMouse(day, 0x0202, 0);
                            else day.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                            Drain();
                            Require(parent.IsOpen && !picker.CalendarPopup.IsOpen && editor.ConfirmButton.IsEnabled, "release closes only calendar and leaves confirmable draft");
                            if (cycle is 0 or 9) Capture(editor, Path.Combine(directory, $"after-reset-{cycle}-endpoint-{endpoint}.png"));
                        }
                        Require(!main.CreateFileColumnEditor("Date").Dates.IsActive, "picking dates does not prematurely commit");
                        var expected = editor.Dates;
                        Invoke(editor.ConfirmButton);
                        Require(!parent.IsOpen && main.CreateFileColumnEditor("Date").Dates == expected, "confirm commits and reopening retains both dates");
                        editor = main.CreateFileColumnEditor("Date");
                        parent = OpenEditor(editor, target);
                        Invoke(editor.ResetButton);
                        Require(!parent.IsOpen && !main.CreateFileColumnEditor("Date").Dates.IsActive, "reset explicitly clears committed date range");
                        log.Add($"cycle={cycle}: confirm / reopen / reset passed");
                    }
                    // Closing while a keyboard/UIA selection has a pending close must not
                    // close the next calendar instance when the dispatcher drains.
                    var final = main.CreateFileColumnEditor("Date");
                    parent = OpenEditor(final, target);
                    Invoke(final.StartDate.Trigger);
                    final.StartDate.Calendar.SelectedDate = DateTime.Today;
                    parent.IsOpen = false; Drain();
                    Require(!final.StartDate.CalendarPopup.IsOpen && !final.EndDate.CalendarPopup.IsOpen, "parent dismissal closes child and cancels pending input work");
                }
                finally
                {
                    var dispose = main.DisposeAsync().AsTask();
                    while (!dispose.IsCompleted) Drain();
                    dispose.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { failure = ex; log.Add(ex.ToString()); }
            finally { if (parent is not null) parent.IsOpen = false; owner?.Close(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        log.Add("foreground unchanged=" + (foreground == GetForegroundWindow()));
        File.WriteAllLines(Path.Combine(directory, "native-date-popup.log"), log);
        if (failure is not null) throw new InvalidOperationException("Native date popup regression failed", failure);

        Console.WriteLine("Native date popup checks passed on private desktop.");
    }

    internal static void LaunchPrivateDesktop(string directory, string childArgument = "--native-date-popups-child", string logName = "native-date-popup.log")
    {
        var inputDesktop = InputDesktopName();
        var name = "BlueLink-Date-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        ProcessInformation process = default;
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = name };
            var host = Environment.ProcessPath!;
            var assembly = typeof(NativeDatePopupVerification).Assembly.Location;
            var command = new System.Text.StringBuilder($"\"{host}\" " + (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"\"{assembly}\" " : "") + $"{childArgument} \"{directory}\"");
            if (!CreateProcess(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref startup, out process))
                throw new System.ComponentModel.Win32Exception();
            if (WaitForSingleObject(process.Process, 60000) != 0)
            {
                TerminateProcess(process.Process, 1);
                throw new TimeoutException("Private desktop date verification exceeded 60 seconds");
            }
            GetExitCodeProcess(process.Process, out var exitCode);
            Require(InputDesktopName() == inputDesktop, "current input desktop is unchanged");
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
            Require(foregroundProcess != process.ProcessId, "test process never occupies input desktop foreground");
            Console.WriteLine(File.ReadAllText(Path.Combine(directory, logName)));
            Require(exitCode == 0, "private desktop date regression failed");
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }
    internal static bool IsPrivateDesktop => DesktopName(GetThreadDesktop(GetCurrentThreadId())).StartsWith("BlueLink-Date-", StringComparison.Ordinal) && DesktopName(GetThreadDesktop(GetCurrentThreadId())) != InputDesktopName();
    private static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return DesktopName(desktop); }
        finally { CloseDesktop(desktop); }
    }
    private static string DesktopName(IntPtr desktop)
    {
        var name = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception();
        return name.ToString();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder data, int length, out int needed);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, System.Text.StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private static Popup OpenEditor(FileFilterEditor editor, FrameworkElement target)
    {
        var popup = new Popup { Child = editor, PlacementTarget = target, Placement = PlacementMode.Bottom,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade };
        editor.ManageCalendarPopups(popup);
        editor.Confirmed += (_, _) => popup.IsOpen = false;
        editor.ResetRequested += (_, _) => popup.IsOpen = false;
        editor.DismissRequested += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => popup.Child = null;
        popup.IsOpen = true; Drain();
        Require(popup.IsOpen, "parent opens");
        return popup;
    }

    private static void Invoke(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Drain();
    }
    internal static void SendMouse(FrameworkElement day, int message, int buttons)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(day) ?? throw new InvalidOperationException("day has no native HWND");
        var point = day.TranslatePoint(new Point(day.ActualWidth / 2, day.ActualHeight / 2), (UIElement)source.RootVisual);
        point = source.CompositionTarget.TransformToDevice.Transform(point);
        SendMessage(source.Handle, message, (IntPtr)buttons, (IntPtr)(((int)point.Y << 16) | ((int)point.X & 0xffff)));
    }
    private static IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
}
