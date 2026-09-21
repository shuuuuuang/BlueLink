using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlueLink.Domain;

namespace BlueLink;

public partial class MainWindow
{
    private bool _earlierHistoryQueued;
    private bool _loadingEarlierHistory;
    private bool _restoringHistoryViewport;
    private long _messageScrollRevision;
    private DateTimeOffset _historyRetryAfter;
    internal Task EarlierHistoryLoad { get; private set; } = Task.CompletedTask;
    internal sealed record MessageViewportAnchor(Guid Id, double Top);

    private void InitializeMessageHistory()
    {
        MessageList.PreviewMouseWheel += (_, args) =>
        {
            if (args.Delta > 0) QueueEarlierHistory();
        };
        MessageList.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is Key.Up or Key.PageUp or Key.Home) QueueEarlierHistory();
        };
        // A drag-selection at the upper edge must finish before a prepend changes row geometry.
        MessageList.PreviewMouseLeftButtonUp += (_, _) => QueueEarlierHistory();
    }

    private void QueueEarlierHistory()
    {
        if (_earlierHistoryQueued || _loadingEarlierHistory || _restoringHistoryViewport || _disposed) return;
        _earlierHistoryQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _earlierHistoryQueued = false;
            EarlierHistoryLoad = LoadEarlierHistoryPreservingViewportAsync();
        }));
    }

    internal async Task<bool> LoadEarlierHistoryPreservingViewportAsync()
    {
        if (_disposed || _loadingEarlierHistory || _restoringHistoryViewport || !_model.ShowMessageSurface ||
            !_model.CanLoadEarlierMessages || DateTimeOffset.UtcNow < _historyRetryAfter ||
            _messageSelectionGesture?.IsInteracting == true || _messageScrollViewer is not { } scroll ||
            scroll.ViewportHeight <= 0 || scroll.VerticalOffset > 240) return false;

        var conversation = _model.MessageHistoryVersion;
        var revision = _messageScrollRevision;
        MessageViewportAnchor? anchor = null;
        _loadingEarlierHistory = true;
        try
        {
            var loaded = await _model.LoadEarlierMessagesAsync(() =>
            {
                // Capture after the query, so scrolling while the database is busy remains respected.
                if (_disposed || !_model.ShowMessageSurface || _messageSelectionGesture?.IsInteracting == true) return false;
                anchor = CaptureMessageViewportAnchor();
                if (anchor is null) return false;
                _restoringHistoryViewport = true;
                _messagePinnedToBottom = false;
                revision = ++_messageScrollRevision;
                return true;
            });
            if (!loaded || anchor is null || !StillCurrent()) return loaded;
            for (var pass = 0; pass < 4 && StillCurrent(); pass++)
            {
                MessageList.UpdateLayout();
                var item = _model.Messages.FirstOrDefault(message => message.Id == anchor.Id);
                if (item is null) break;
                var row = MessageHistoryRows().FirstOrDefault(value => value.Id == anchor.Id);
                if (row is null)
                {
                    MessageList.ScrollIntoView(item);
                }
                else
                {
                    var shift = row.Top - anchor.Top;
                    if (Math.Abs(shift) <= 0.5) break;
                    scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + shift, 0, scroll.ScrollableHeight));
                }
                await Dispatcher.InvokeAsync(() => MessageList.UpdateLayout(), DispatcherPriority.Loaded);
            }
            return loaded;
        }
        catch (Exception error)
        {
            Session.SessionLog.Write("History", "Automatic earlier messages failed", error);
            if (!_disposed && conversation == _model.MessageHistoryVersion)
            {
                _historyRetryAfter = DateTimeOffset.UtcNow.AddSeconds(3);
                ShowToast(Localization.Strings.Get("查询失败，请重试"), ToastLevel.Error);
            }
            return false;
        }
        finally
        {
            _loadingEarlierHistory = false;
            _restoringHistoryViewport = false;
            if (!_disposed && conversation == _model.MessageHistoryVersion)
            {
                _messagePinnedToBottom = scroll.ScrollableHeight <= 1 || scroll.VerticalOffset >= scroll.ScrollableHeight - 2;
                if (_messagePinnedToBottom) NewMessagesButton.Visibility = Visibility.Collapsed;
            }
        }

        bool StillCurrent() => !_disposed && conversation == _model.MessageHistoryVersion && revision == _messageScrollRevision;
    }

    internal MessageViewportAnchor? CaptureMessageViewportAnchor()
    {
        var viewport = FindVisualChild<ScrollContentPresenter>(MessageList);
        if (viewport is null) return null;
        var top = viewport.TranslatePoint(new Point(), MessageList).Y;
        var bottom = top + viewport.ActualHeight;
        return MessageHistoryRows().Where(row => row.Bottom > top && row.Top < bottom)
            .OrderBy(row => row.Top).Select(row => new MessageViewportAnchor(row.Id, row.Top)).FirstOrDefault();
    }

    private sealed record MessageHistoryRow(Guid Id, double Top, double Bottom);

    private IEnumerable<MessageHistoryRow> MessageHistoryRows()
    {
        foreach (var container in MessageHistoryChildren<ListBoxItem>(MessageList))
        {
            if (container.DataContext is not ChatItem item) continue;
            var bubble = MessageHistoryChildren<Border>(container).FirstOrDefault(border => border.Name == "Bubble");
            if (bubble is null || bubble.ActualHeight <= 0) continue;
            var top = bubble.TranslatePoint(new Point(), MessageList).Y;
            yield return new(item.Id, top, top + bubble.ActualHeight);
        }
    }

    private static IEnumerable<T> MessageHistoryChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in MessageHistoryChildren<T>(child)) yield return descendant;
        }
    }
}
