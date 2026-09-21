using System.Windows;
using BlueLink.Security;
using BlueLink.Transport;
using BlueLink.Domain;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private IdentityAssociationHandler CreateIdentityAssociationHandler(IPeerConnection connection) => new(
        (peerId, token) => _database.FindIdentityCandidateAsync(peerId, connection.IdentityHint, token),
        candidate => !_resettingIdentity && Volatile.Read(ref _disposeStarted) == 0 &&
            !_sessions.Snapshot().Any(session => session.PeerId?.Equals(candidate.PeerId, StringComparison.OrdinalIgnoreCase) == true &&
                session.Phase == ConnectionPhase.Connected),
        ApplyIdentityAssociationsAsync);

    private async Task ApplyIdentityAssociationsAsync()
    {
        await _draftSaveGate.WaitAsync();
        _draftsReady = false;
        await Application.Current.Dispatcher.InvokeAsync(() => Raise(nameof(CanEditDraft)));
        if (!await PersistDraftsAsync())
        {
            _draftsReady = true;
            await Application.Current.Dispatcher.InvokeAsync(() => Raise(nameof(CanEditDraft)));
            _draftSaveGate.Release();
            throw new System.IO.IOException("Drafts could not be persisted before identity association.");
        }
        // Drain old writes in the same order used by message persistence.
        await _conversationPersistenceGate.WaitAsync();
        try
        {
            await _trustMutationGate.WaitAsync();
            try
            {
                await _database.ApplyIdentityAssociationsAsync(_identity);
                var peers = await _database.LoadPeersAsync();
                var conversations = await _database.LoadConversationsAsync();
                var transfers = await LoadStoredTransfersAsync(null);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _storedPeers.Clear();
                    foreach (var peer in peers) _storedPeers[peer.PeerId] = peer;
                    _storedConversations.Clear();
                    foreach (var conversation in conversations) _storedConversations[conversation.PeerId] = conversation;
                    LoadDrafts();
                    var aliases = _identity.IdentityAssociations;
                    while (_activePeerId is { } id && aliases.TryGetValue(id, out var next)) _activePeerId = next;
                    foreach (var transfer in AllTransfers.Where(value => aliases.ContainsKey(value.PeerId ?? "")).ToArray()) AllTransfers.Remove(transfer);
                    foreach (var transfer in transfers.Where(value => !AllTransfers.Any(existing => existing.Id == value.Id))) AllTransfers.Add(transfer);
                    foreach (var retired in aliases.Keys) Notifications.Read(retired);
                    RefreshConversations();
                    foreach (var conversation in Conversations.Where(value => aliases.Values.Contains(value.PeerId, StringComparer.OrdinalIgnoreCase)))
                        Notifications.Restore(conversation.PeerId, conversation.PeerName, conversation.UnreadCount, conversation.LastActivityAt);
                });
            }
            finally { _trustMutationGate.Release(); }
        }
        finally
        {
            _conversationPersistenceGate.Release();
            _draftSaveGate.Release();
            _draftsReady = true;
            await Application.Current.Dispatcher.InvokeAsync(() => { Raise(nameof(CanEditDraft)); Raise(nameof(DraftText)); });
        }
    }
}
