using System.IO;
using BlueLink.Security;
using BlueLink.Storage;

internal static class MessageRecoveryVerification
{
    internal static async Task RunAsync(string output)
    {
        var root = Path.Combine(output, "message-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identity = new IdentityStore(root);
        var database = new BlueLinkDatabase(root, root);
        await database.InitializeAsync(identity);
        const string peer = "qa-message-recovery";
        await database.UpsertPeerAsync(new(peer, "QA", "Android", StoredTrustState.Unknown, null, 1, 1, 1));
        await database.UpsertConversationAsync(new("peer:" + peer, peer, 1, 0));
        await database.UpdateDraftAsync(peer, "  Draft 🌍\nkeep  ");
        var statuses = new[] { "Sending", "LocalQueued", "LOCAL_QUEUED", "Sent", "Delivered", "Read", "Failed", "Received" };
        for (var index = 0; index < statuses.Length; index++)
            await database.UpsertMessageAsync(new(new Guid(index + 1, 0, 0, new byte[8]).ToString("N"), "peer:" + peer, peer,
                index == 7 ? StoredMessageDirection.Incoming : StoredMessageDirection.Outgoing, StoredMessageType.Text,
                "  exact 🌍 " + index, statuses[index], index + 1, index + 1));
        await database.InitializeAsync(identity);
        await database.InitializeAsync(identity);
        var rows = await database.LoadMessagesAsync("peer:" + peer);
        if (rows.Count != statuses.Length) throw new Exception("Recovery changed message count");
        for (var index = 0; index < statuses.Length; index++)
        {
            var item = rows.Single(x => x.Content == "  exact 🌍 " + index);
            if (item.Status != (index < 3 ? "Failed" : statuses[index]) || item.CreatedAt != index + 1)
                throw new Exception("Recovery changed a delivered message or failed to recover a queue");
        }
        var drafts = (await database.LoadConversationsAsync()).ToDictionary(x => x.PeerId, x => x.Draft);
        if (drafts.GetValueOrDefault(peer) != "  Draft 🌍\nkeep  ") throw new Exception("Recovery lost a draft");
        Console.WriteLine("Message recovery: 10 checks passed; interrupted queues require explicit resend, history and drafts preserved.");
    }
}
