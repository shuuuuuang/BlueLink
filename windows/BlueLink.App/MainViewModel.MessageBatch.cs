using BlueLink.Domain;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private bool _messageSelectionMode;
    public bool MessageSelectionMode { get => _messageSelectionMode; internal set { if (_messageSelectionMode == value) return; _messageSelectionMode = value; Raise(); } }

    internal async Task<IReadOnlyList<MessageBatchResult>> DeleteSelectedMessagesAsync(string peerId, IEnumerable<Guid> selection)
    {
        var ids = selection.Distinct().ToArray();
        var results = new List<MessageBatchResult>();
        await _conversationPersistenceGate.WaitAsync();
        try
        {
            var history = ids.Any(id => !Messages.Any(message => message.Id == id)) && ActivePeerId == peerId
                ? (await LoadSearchHistoryAsync(peerId)).ToDictionary(message => message.Id) : new Dictionary<Guid, ChatItem>();
            foreach (var id in ids)
            {
                var item = ActivePeerId == peerId
                    ? Messages.FirstOrDefault(message => message.Id == id) ?? history.GetValueOrDefault(id) : null;
                if (item is null) { results.Add(new(id, false, "记录已不存在或已切换会话")); continue; }
                var active = !MessageBatch.CanDelete(item) || item.Attachments?.Any(file =>
                    AllTransfers.FirstOrDefault(transfer => transfer.Id == file.TransferId) is { } transfer &&
                    (transfer.IsActive || transfer.RecoveryPending)) == true;
                if (active) { results.Add(new(id, false, "正在发送或待恢复的消息不能删除")); continue; }
                try
                {
                    await DeleteMessageAsync(item);
                    if (Messages.FirstOrDefault(message => message.Id == id) is { } refreshed) Messages.Remove(refreshed);
                    results.Add(new(id, true));
                }
                catch (Exception error) { results.Add(new(id, false, error.Message)); }
            }
        }
        finally { _conversationPersistenceGate.Release(); }
        return results;
    }
}
