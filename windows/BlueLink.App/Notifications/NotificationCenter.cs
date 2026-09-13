using System.Collections.ObjectModel;
using BlueLink.Localization;

namespace BlueLink.Notifications;

public sealed record NotificationConversation(string PeerId, string PeerName, string Preview, int Count, DateTimeOffset UpdatedAt)
{
    public string CountText => Count > 99 ? "99+" : Count.ToString();
}
public sealed class NotificationCenter
{
    private readonly Dictionary<string, NotificationConversation> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = [];
    public ObservableCollection<NotificationConversation> Conversations { get; } = [];
    public int UnreadCount => _peers.Values.Sum(value => value.Count);
    public string Title => Strings.Format($"蓝联 · {UnreadCount} 条新消息");
    public string Subtitle => Strings.Format($"来自 {_peers.Count} 个会话");
    public event Action? Changed;
    public void Receive(string peerId, string name, Guid messageId, string preview, bool conversationVisible)
    {
        if (!_seen.Add(messageId)) return;
        _seenOrder.Enqueue(messageId);
        while (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
        if (conversationVisible) return;
        var old = _peers.GetValueOrDefault(peerId);
        _peers[peerId] = new(peerId, name, Shorten(preview), Math.Min(9999, (old?.Count ?? 0) + 1), DateTimeOffset.UtcNow);
        Refresh();
    }
    public void UpdatePreview(string peerId, string preview)
    {
        if (!_peers.TryGetValue(peerId, out var old)) return;
        _peers[peerId] = old with { Preview = Shorten(preview) }; Refresh();
    }
    public void Restore(string peerId, string name, int count, DateTimeOffset updated)
    {
        if (count <= 0 || _peers.ContainsKey(peerId)) return;
        _peers[peerId] = new(peerId, name, Strings.Get("打开会话查看未读消息"), Math.Min(9999, count), updated); Refresh();
    }
    public void Read(string peerId) { if (_peers.Remove(peerId)) Refresh(); }
    public void Clear() { _peers.Clear(); Refresh(); }
    public void Refresh()
    {
        Conversations.Clear(); foreach (var item in _peers.Values.OrderByDescending(value => value.UpdatedAt)) Conversations.Add(item);
        Changed?.Invoke();
    }
    private static string Shorten(string value) => value.Replace('\r', ' ').Replace('\n', ' ') is var text && text.Length > 140 ? text[..140] + "…" : text;
}
