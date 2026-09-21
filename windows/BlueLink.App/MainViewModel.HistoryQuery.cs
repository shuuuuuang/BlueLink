using BlueLink.Domain;

namespace BlueLink;

public sealed partial class MainViewModel
{
    internal async Task<List<ChatItem>> LoadSearchHistoryAsync(string peerId)
    {
        var stored = await LoadStoredMessagesAsync(peerId, all: true);
        var current = string.Equals(_activePeerId,peerId,StringComparison.OrdinalIgnoreCase) ? Messages.ToArray() : [];
        return await Task.Run(() => stored.Concat(current).GroupBy(item=>item.Id).Select(group=>group.Last()).ToList());
    }

    internal async Task<bool> LoadMessageContextAsync(string peerId, Guid id)
    {
        if (!string.Equals(_activePeerId,peerId,StringComparison.OrdinalIgnoreCase)) return false;
        var version = _conversationSelectionVersion;
        var page = await LoadStoredMessagesAsync(peerId,id.ToString("N"),around:true);
        if(version != _conversationSelectionVersion || !string.Equals(_activePeerId,peerId,StringComparison.OrdinalIgnoreCase)) return false;
        if(!page.Any(item=>item.Id==id)) return Messages.Any(item=>item.Id==id);
        // Preserve live/unpersisted messages while adding the bounded context window.
        var merged = page.Concat(Messages).GroupBy(item=>item.Id).Select(group=>group.Last())
            .OrderBy(item=>item.CreatedAt).ThenBy(item=>item.Id.ToString("N"),StringComparer.Ordinal).ToArray();
        Messages.Clear(); foreach(var item in merged) Messages.Add(item);
        return true;
    }

    private bool _loadingEarlierMessages;
    private (long Version, Guid First)? _exhaustedHistoryStart;
    internal long MessageHistoryVersion => _conversationSelectionVersion;
    internal bool IsPrependingMessages { get; private set; }
    internal bool CanLoadEarlierMessages => _activePeerId is not null && Messages.FirstOrDefault() is { } first &&
        _exhaustedHistoryStart != (_conversationSelectionVersion, first.Id);

    internal async Task<bool> LoadEarlierMessagesAsync(Func<bool>? beforeInsert = null)
    {
        if (_loadingEarlierMessages || !CanLoadEarlierMessages || _activePeerId is not { } peer || Messages.FirstOrDefault() is not { } first) return false;
        var version = _conversationSelectionVersion;
        _loadingEarlierMessages = true;
        try
        {
            var page = await LoadStoredMessagesAsync(peer, first.Id.ToString("N"));
            if (version != _conversationSelectionVersion || !string.Equals(_activePeerId, peer, StringComparison.OrdinalIgnoreCase) ||
                Messages.FirstOrDefault()?.Id != first.Id) return false;
            var ids = Messages.Select(item => item.Id).ToHashSet();
            var earlier = page.Where(item => !ids.Contains(item.Id)).ToArray();
            if (earlier.Length == 0)
            {
                _exhaustedHistoryStart = (version, first.Id);
                return false;
            }
            if (beforeInsert?.Invoke() == false) return false;
            IsPrependingMessages = true;
            for (var i = earlier.Length - 1; i >= 0; i--) Messages.Insert(0, earlier[i]);
            return true;
        }
        finally { IsPrependingMessages = false; _loadingEarlierMessages = false; }
    }
}
