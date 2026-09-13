using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BlueLink;

public enum ToastLevel { Info, Success, Warning, Error }

public sealed record ToastNotice(string Message, ToastLevel Level, DateTimeOffset ExpiresAt)
{
    public override string ToString() => Message;
}

public partial class ToastHost : UserControl, IDisposable
{
    private readonly DispatcherTimer _timer;
    internal ObservableCollection<ToastNotice> Items { get; } = [];
    private bool _disposed;

    public ToastHost()
    {
        InitializeComponent();
        Notices.ItemsSource = Items;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => Expire(DateTimeOffset.UtcNow), Dispatcher);
        _timer.Stop();
    }

    internal static TimeSpan Duration(ToastLevel level) => TimeSpan.FromSeconds(level is ToastLevel.Warning or ToastLevel.Error ? 5 : 3);

    internal void Show(string message, ToastLevel level = ToastLevel.Info, TimeSpan? lifetime = null, DateTimeOffset? now = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        var startedAt = now ?? DateTimeOffset.UtcNow;
        Expire(startedAt);
        while (Items.Count >= 4) Items.RemoveAt(0);
        Items.Add(new(Localization.Strings.Get(message), level, startedAt + (lifetime ?? Duration(level))));
        _timer.Start();
    }

    internal void Expire(DateTimeOffset now)
    {
        for (var index = Items.Count - 1; index >= 0; index--)
            if (Items[index].ExpiresAt <= now) Items.RemoveAt(index);
        if (Items.Count == 0) _timer.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        Items.Clear();
    }
}
