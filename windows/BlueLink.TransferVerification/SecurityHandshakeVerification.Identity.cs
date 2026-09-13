using System.IO;
using BlueLink.Security;
using BlueLink.Storage;

internal sealed partial class SecurityHandshakeVerification
{
    private async Task IdentityAssociation(string root)
    {
        Check(PeerIdentityHint.Bluetooth("AA:bb:CC:dd:EE:ff") == "bt:AABBCCDDEEFF", "Bluetooth MAC canonicalization");
        foreach (var bad in new[] { "", "Windows PC", "00:00:00:00:00:00", "02:00:00:00:00:00", "usb:port1" })
            Check(PeerIdentityHint.Bluetooth(bad) is null, "non-address cannot select a device: " + bad);
        Check(PeerIdentityHint.UsbSerial("ccc032bc") == "usb-serial:CCC032BC" && PeerIdentityHint.UsbSerial("6&123&0&0000") is null,
            "USB serial is distinct from port-derived instance IDs");
        var stable = Store(root, "usb-id"); var usbId = stable.UsbHostId; stable.ResetIdentity();
        Check(stable.UsbHostId == usbId && Store(root, "usb-id").UsbHostId == usbId, "USB host ID survives identity reset and restart");

        foreach (var mode in new[] { "confirm", "cancel", "remote-reject", "revoke", "timeout", "disk-failure", "busy", "recover" })
        {
            var local = Store(root, "associate-" + mode);
            var remote = Store(root, "remote-" + mode);
            var old = DeviceIdentity.Generate();
            var oldId = Convert.ToHexString(old.PeerId); var newId = Convert.ToHexString(remote.Identity.PeerId);
            var db = new BlueLinkDatabase(Path.Combine(root, "db-" + mode), root);
            await db.InitializeAsync(local);
            local.Trust(old.PeerId, old.PublicKey);
            const string mac = "AA:BB:CC:DD:EE:FF";
            var hint = PeerIdentityHint.Bluetooth(mac)!;
            await db.UpsertPeerAsync(new(oldId, "原电脑备注", "Windows", StoredTrustState.Trusted, old.PublicKey, 100, 200, 200, mac));
            await db.RecordIdentityHintAsync(oldId, hint);
            var oldConversation = "peer:" + oldId.ToLowerInvariant();
            await db.UpsertConversationAsync(new(oldConversation, oldId, 400, 3, "保留的草稿"));
            await db.UpsertMessageAsync(new("old-message", oldConversation, oldId, StoredMessageDirection.Incoming, StoredMessageType.Text, "原消息", "Read", 300, 300));
            await db.UpsertMessageAsync(new("queued-message", oldConversation, oldId, StoredMessageDirection.Outgoing, StoredMessageType.Text, "未发送", "LocalQueued", 301, 301));
            await db.UpsertAttachmentAsync(new("attachment", "old-message", "old-transfer", "文件.png", "image/png", 99, [1,2], "qa:kept-file", "qa:kept-preview", "Completed"));
            await db.UpsertTransferAsync(new("old-transfer", oldId, "old-message", "Incoming", "Completed", "文件.png", "image/png", 99, 99,
                "qa:kept-file", null, [1,2], null, null, 300, 350));
            await db.UpsertTransferAsync(new("unfinished-transfer", oldId, null, "Outgoing", "Paused", "未完成.bin", "application/octet-stream", 100, 25,
                "qa:source", "qa:snapshot", null, null, null, 300, 350));
            var candidate = await db.FindIdentityCandidateAsync(newId, hint);
            Check(candidate?.PeerId == oldId, mode + ": exact known MAC selects original peer");
            var applied = 0;
            var handler = new IdentityAssociationHandler((id, token) => db.FindIdentityCandidateAsync(id, hint, token), _ => mode != "busy", async () =>
            {
                applied++;
                if (mode == "recover") throw new IOException("Simulated interruption before history migration");
                await db.ApplyIdentityAssociationsAsync(local);
            });
            await using (var pair = new Pair(local, remote, mode == "timeout" ? TimeSpan.FromMilliseconds(800) : null, oldId, handler))
            {
                if (mode == "busy")
                {
                    Check((await pair.A).Error?.Stage == TrustStage.Revoked && local.FindTrustedKey(newId) is null &&
                        local.IdentityAssociations.Count == 0 && (await db.LoadPeersAsync()).Count == 1,
                        "active original session blocks association without creating a second device");
                    continue;
                }
                var a = await pair.ARequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var b = await pair.BRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(a.IdentityCandidate?.PeerId == oldId && a.SafetyCode == b.SafetyCode, mode + ": explicit association has fresh matching safety codes");
                Check(local.FindTrustedKey(oldId) is not null && local.IdentityAssociations.Count == 0 && (await db.LoadPeersAsync()).Count == 1,
                    mode + ": no trust or record mutation before decision");
                using var blocked = mode == "disk-failure" ? new FileStream(Path.Combine(root, "associate-" + mode, "identity.json.tmp"),
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
                if (mode == "cancel") a.Cancel();
                else if (mode == "remote-reject") b.Cancel();
                else if (mode == "timeout") { }
                else
                {
                    a.Confirm();
                    Check(local.IdentityAssociations.Count == 0, mode + ": one-sided confirmation cannot migrate history");
                    if (mode == "revoke") local.RemoveTrust(oldId);
                    b.Confirm();
                }
                var result = await pair.A;
                if (mode is "confirm" or "recover")
                {
                    Check(mode == "recover" ? result.Error is not null : result.Error is null, mode + ": expected handshake outcome");
                    Check(local.FindTrustedKey(oldId) is null && local.FindTrustedKey(newId) is not null && local.IsRetired(oldId), mode + ": old trust replaced once");
                    Check(local.IdentityAssociations.GetValueOrDefault(oldId) == newId && applied == 1, mode + ": durable approved migration journal");
                }
                else
                {
                    Check(result.Error is not null && local.FindTrustedKey(newId) is null && local.IdentityAssociations.Count == 0 && applied == 0,
                        mode + ": unsuccessful verification cannot pin or associate");
                    Check((await db.LoadMessagesAsync(oldConversation)).Count == 2, mode + ": original history untouched");
                    if (mode != "revoke") Check(local.FindTrustedKey(oldId) is not null, mode + ": original trust retained");
                }
            }
            if (mode is not ("confirm" or "recover")) continue;
            var reloaded = Store(root, "associate-" + mode);
            await db.InitializeAsync(reloaded);
            await db.ApplyIdentityAssociationsAsync(reloaded);
            var peer = (await db.LoadPeersAsync()).Single();
            Check(peer.PeerId == newId && peer.DisplayName == "原电脑备注" && peer.CreatedAt == 100, mode + ": one card retains original metadata");
            var conversation = (await db.LoadConversationsAsync()).Single();
            Check(conversation.PeerId == newId && conversation.UnreadCount == 3 && conversation.Draft == "保留的草稿", mode + ": unread count and draft survive repeated recovery");
            var messages = await db.LoadMessagesAsync(conversation.ConversationId);
            Check(messages.Count == 2 && messages.All(value => value.PeerId == newId) && messages.Single(value => value.MessageId == "queued-message").Status == "Failed",
                mode + ": history moves but queued messages cannot auto-send");
            var attachment = (await db.LoadAttachmentsAsync("old-message")).Single();
            Check(attachment.LocalPath == "qa:kept-file" && attachment.PreviewPath == "qa:kept-preview" && attachment.Sha256!.SequenceEqual(new byte[] {1,2}), mode + ": attachment paths and hashes remain intact");
            var transfers = await db.LoadTransfersAsync(newId);
            Check(transfers.Single(value => value.TransferId == "old-transfer").Status == "Completed" &&
                transfers.Single(value => value.TransferId == "unfinished-transfer").Status == "Failed", mode + ": completed transfers retained and unfinished work stopped");
            await db.UpsertTransferAsync(new("unfinished-transfer", oldId, null, "Outgoing", "Transferring", "未完成.bin", "application/octet-stream", 100, 50,
                "qa:source", "qa:snapshot", null, null, null, 300, 999));
            Check((await db.LoadTransfersAsync(newId)).Single(value => value.TransferId == "unfinished-transfer").Status == "Failed", mode + ": late old-identity progress cannot revive migrated transfer");
            await db.UpsertMessageAsync(new("queued-message", oldConversation, oldId, StoredMessageDirection.Outgoing, StoredMessageType.Text, "late update", "LocalQueued", 301, 301));
            Check((await db.LoadMessagesAsync(conversation.ConversationId)).Single(value => value.MessageId == "queued-message").Status == "Failed", mode + ": late old-identity message cannot restore auto-send state");
            Check(!reloaded.TryTrust(old.PeerId, old.PublicKey, reloaded.RevocationVersion), mode + ": retired identity cannot be silently trusted again");
            await db.ClearPeerTrustAsync();
            Check((await db.LoadPeersAsync()).Count == 1, mode + ": removing trust cannot revive the retired device card");
            await db.InitializeAsync(reloaded);
            Check((await db.LoadPeersAsync()).Count == 1, mode + ": replay after trust projection reset remains idempotent");
            var another = DeviceIdentity.Generate(); var anotherId = Convert.ToHexString(another.PeerId);
            await db.UpsertPeerAsync(new(anotherId, "原电脑备注", "Windows", StoredTrustState.Unknown, another.PublicKey, 500, 500, TransportAddress: mac));
            Check(await db.FindIdentityCandidateAsync("new-unknown", hint) is null, mode + ": duplicated MAC cannot auto-select a candidate");
            Check(await db.FindIdentityCandidateAsync("new-unknown", "bt:112233445566") is null, mode + ": matching display names do not prove continuity");
        }
    }
}
