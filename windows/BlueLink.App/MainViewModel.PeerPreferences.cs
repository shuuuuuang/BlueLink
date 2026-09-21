using BlueLink.Domain;
using BlueLink.Storage;
namespace BlueLink;
public sealed partial class MainViewModel
{
    private PeerPreferences? _peerPreferences;
    private PeerPreferences PeerPreferences => _peerPreferences ??= new(DataDirectory);
    internal async Task SavePeerPreferenceAsync(ConversationSummary peer, string? note = null, bool? pinned = null)
    {
        await Task.Run(() => PeerPreferences.Update(peer.PeerId,note,pinned));
        RefreshConversations(); Raise(nameof(ActivePeerTitle)); Raise(nameof(WorkspaceTitle));
    }
}
