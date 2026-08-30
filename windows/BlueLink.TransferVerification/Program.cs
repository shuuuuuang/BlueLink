using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BlueLink.Security;
using BlueLink.Storage;
using BlueLink.Protocol;
using BlueLink.Transfer;
using BlueLink.Domain;
using BlueLink.Files;

var verification = new TransferReceiverVerification();
await verification.RunAsync();
await new LocalStorageVerification().RunAsync();
new IdentityRecoveryVerification().Run();
new BtxProtocolVerification().Run();
new FileDragDropVerification().Run();
new ChatTimeVerification().Run();
await new OutgoingSnapshotVerification().RunAsync();

internal sealed class ChatTimeVerification
{
    public void Run()
    {
        var now = DateTimeOffset.Now;
        Check(Create(now.Date.AddHours(9).AddMinutes(7)).GroupTimeText == "09:07", "today uses HH:mm");
        Check(Create(now.Date.AddDays(-1).AddHours(8).AddMinutes(6)).GroupTimeText == "昨天 08:06", "yesterday label");
        Check(Create(new DateTimeOffset(new DateTime(now.Year - 1, 12, 31, 7, 5, 0), now.Offset)).GroupTimeText ==
              $"{now.Year - 1}年12月31日 07:05", "previous year label");
        var olderThisYear = new DateTimeOffset(new DateTime(now.Year, 1, 2, 6, 4, 0), now.Offset);
        if (olderThisYear.Date < now.Date.AddDays(-7))
            Check(Create(olderThisYear).GroupTimeText == "01月02日 06:04", "same year label");
        Console.WriteLine("BlueLink Windows chat timestamp verification passed");
    }

    private static ChatItem Create(DateTimeOffset timestamp) =>
        new(Guid.NewGuid(), "test", false, timestamp, MessageStatus.Received);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class OutgoingSnapshotVerification
{
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkSnapshotVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "mutable.bin");
            var original = Enumerable.Range(0, 130_000).Select(value => (byte)(value % 251)).ToArray();
            await File.WriteAllBytesAsync(source, original);
            var snapshot = await OutgoingSnapshot.CreateAsync(source, Path.Combine(root, "cache"), Guid.NewGuid(),
                CancellationToken.None);
            await File.WriteAllTextAsync(source, "mutated after snapshot");
            Check(File.ReadAllBytes(snapshot).SequenceEqual(original),
                "snapshot bytes must not follow later source mutations");
            Check(new FileInfo(snapshot).Length == original.LongLength,
                "snapshot must preserve the exact source length");
            Console.WriteLine("BlueLink Windows outgoing snapshot verification passed: 2 checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class FileDragDropVerification
{
    public void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkFileDropVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.txt");
            var second = Path.Combine(root, "second.bin");
            File.WriteAllText(first, "BlueLink");
            File.WriteAllBytes(second, [1, 2, 3, 4]);
            var files = FileDragDropService.NormalizeFilePaths([first, first.ToUpperInvariant(), root, second, ""]);
            Check(files.Count == 2, "file drops must reject directories and de-duplicate paths");
            Check(files[0] == Path.GetFullPath(first) && files[1] == Path.GetFullPath(second),
                "file drops must preserve the user's order");
            Check(FileDragDropService.TotalBytes(files) == 12, "file drop byte summary");

            var complete = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "first.txt", "text/plain", 8,
                first, "Completed");
            var incomplete = complete with { State = "Transferring" };
            Check(FileDragDropService.CanCopyOut(complete), "completed local attachments can be dragged out");
            Check(!FileDragDropService.CanCopyOut(incomplete), "incomplete attachments cannot be dragged out");
            Console.WriteLine("BlueLink Windows file drag/drop verification passed: 5 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class IdentityRecoveryVerification
{
    public void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkIdentityVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = new IdentityStore(root);
            var originalPeerId = original.Identity.PeerId;
            original.Trust(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(44));

            var path = Path.Combine(root, "identity.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["PrivateKeyProtected"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            File.WriteAllText(path, json.ToJsonString());

            var recovered = new IdentityStore(root);
            Check(recovered.RecoveredCorruptIdentity, "corrupt DPAPI identity must be recovered");
            Check(recovered.RecoveryBackupPath is not null && File.Exists(recovered.RecoveryBackupPath),
                "unreadable identity must be backed up");
            Check(!CryptographicOperations.FixedTimeEquals(originalPeerId, recovered.Identity.PeerId),
                "recovery must generate a new identity");
            Check(recovered.TrustedIdentities.Count == 0,
                "identity recovery must require peers to be trusted again");
            Console.WriteLine("BlueLink Windows identity recovery verification passed: 4 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class BtxProtocolVerification
{
    public void Run()
    {
        Check(Convert.ToHexString(ProtocolGreeting.Current.Encode()) == "010100000000001F",
            "BTX/1.1 greeting vector");
        var legacy = ProtocolGreeting.Decode([1, 0, 0, 0]);
        var downgrade = ProtocolGreeting.Current.Negotiate(legacy);
        Check(downgrade.Minor == 0 && downgrade.Capabilities == BtxCapability.None,
            "BTX/1.0 capability downgrade");

        var hash = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var message = new ChatEnvelope(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ChatPayloadKind.File,
            1_700_000_000_123L, "BTX 1.1",
            [new(Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), AttachmentRole.File,
                "report.pdf", "application/pdf", 123_456L, hash)]);
        var encoded = MessageWire.Encode(message);
        Check(Convert.ToHexString(encoded) ==
            "424D010300112233445566778899AABBCCDDEEFF0000018BCFE5687B0000000742545820312E310111111111222233334444555555555555AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE00000000000001E240000A7265706F72742E706466000F6170706C69636174696F6E2F70646620000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
            "cross-platform BTX/1.1 attachment message vector");
        var decoded = MessageWire.Decode(encoded);
        Check(decoded.MessageId == message.MessageId && decoded.Attachments.Count == 1 &&
            decoded.Attachments[0].Sha256!.SequenceEqual(hash), "structured attachment message round trip");

        var receipt = new ChatReceipt(message.MessageId, ReceiptState.Delivered, 1_700_000_000_321L);
        Check(MessageWire.DecodeReceipt(MessageWire.EncodeReceipt(receipt)) == receipt,
            "message receipt round trip");

        var control = new TransferControl(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TransferControlAction.Cancel, 1_700_000_000_456L, "user canceled");
        var encodedControl = TransferControlWire.Encode(control);
        Check(Convert.ToHexString(encodedControl) ==
            "425401AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE030000018BCFE569C8000D757365722063616E63656C6564",
            "cross-platform BTX/1.1 transfer control vector");
        Check(TransferControlWire.Decode(encodedControl) == control, "transfer control round trip");

        var offer = new FileOffer(control.TransferId, "report.pdf", 123_456, 65_536, hash,
            message.MessageId, message.Attachments[0].AttachmentId, "application/pdf", AttachmentRole.File);
        var decodedOffer = TransferWire.DecodeOffer(TransferWire.EncodeOffer(offer));
        Check(decodedOffer.Id == offer.Id && decodedOffer.MessageId == offer.MessageId &&
            decodedOffer.AttachmentId == offer.AttachmentId && decodedOffer.Name == offer.Name &&
            decodedOffer.MimeType == offer.MimeType && decodedOffer.Role == offer.Role &&
            decodedOffer.Hash.SequenceEqual(hash), "enhanced file offer round trip");

        var accept = new FileTransferAccept(offer.Id, 7);
        Check(TransferWire.DecodeAccept(TransferWire.EncodeAccept(accept)) == accept,
            "resume-aware transfer accept round trip");
        Check(TransferWire.DecodeAccept(TransferWire.EncodeId(offer.Id)).NextExtent == 0,
            "legacy transfer accept maps to a zero resume extent");

        var truncatedRejected = false;
        try { MessageWire.Decode(encoded[..^1]); }
        catch (IOException) { truncatedRejected = true; }
        Check(truncatedRejected, "truncated structured message rejection");
        Check(WireMessagePriority.Of(WireMessageType.TransferControl) == 0,
            "transfer control bypasses bulk frames");
        Console.WriteLine("BlueLink BTX/1.1 Windows verification passed: 12 checks");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class LocalStorageVerification
{
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkStorageVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new BlueLinkDatabase(root, root);
            await database.InitializeAsync(new IdentityStore(root));

            var settings = BlueLinkSettings.Defaults(Path.Combine(root, "inbox")) with
            {
                MaxConcurrentConnections = 6,
                ReceiveSizeLimitBytes = 123_456_789,
                TransferPanelExpanded = false,
            };
            await database.SaveSettingsAsync(settings);
            var loadedSettings = await database.LoadSettingsAsync();
            Check(loadedSettings == settings, "settings must round-trip through SQLite");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string peerId = "0123456789ABCDEF0123456789ABCDEF";
            await database.UpsertPeerAsync(new(peerId, "Android test", "Android", StoredTrustState.Trusted, [1, 2, 3], now, now, now));
            await database.UpsertConversationAsync(new("conversation-test", peerId, now, 2, "draft"));
            await database.UpsertMessageAsync(new("message-test", "conversation-test", peerId,
                StoredMessageDirection.Incoming, StoredMessageType.File, "payload", "Delivered", now, 1));
            await database.UpsertAttachmentAsync(new("attachment-test", "message-test", "transfer-test",
                "photo.png", "image/png", 3, [4, 5, 6], "photo.png", "preview.png", "Completed"));
            await database.UpsertTransferAsync(new("transfer-test", peerId, "message-test", "Incoming", "Completed",
                "photo.png", "image/png", 3, 3, "photo.png", null, [4, 5, 6], null, null, now, now));
            await database.UpsertExtentAsync(new("transfer-test", 0, 0, 3, [4, 5, 6], "Verified"));

            Check((await database.LoadPeersAsync()).Any(peer => peer.PeerId == peerId && peer.DisplayName == "Android test"),
                "peer must persist");
            Check((await database.LoadConversationsAsync()).Any(conversation => conversation.ConversationId == "conversation-test"),
                "conversation must persist");
            Check((await database.LoadMessagesAsync("conversation-test")).Single().Type == StoredMessageType.File,
                "message must persist in order");
            Check((await database.LoadTransfersAsync(peerId)).Single().Status == "Completed",
                "transfer must persist");
            Console.WriteLine("BlueLink Windows SQLite verification passed: 5 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class TransferReceiverVerification
{
    private int _checks;

    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkTransferVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CompletesAfterHashStreamIsClosed(root);
            await IsolatesConcurrentSameNameTransfers(root);
            await PreservesAndRetriesACommitBlockedByAnotherHandle(root);
            await ResumesAfterReceiverRestart(root);
            Console.WriteLine($"BlueLink Windows transfer verification passed: {_checks} checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private async Task CompletesAfterHashStreamIsClosed(string root)
    {
        var data = CreateBytes(183_501, 20260826);
        var offer = Offer("android-screenshot.jpg", data);
        await using var receiver = new TransferReceiver(root, offer);
        await WriteExtents(receiver, offer, data);
        var committing = false;
        var target = await receiver.FinishAsync(CancellationToken.None, () => committing = true);

        Check(committing, "commit callback must run after hash verification");
        Check(File.Exists(target), "verified file must be committed");
        Check(File.ReadAllBytes(target).SequenceEqual(data), "committed bytes must match the sender");
        Check(!Directory.EnumerateFiles(root, "android-screenshot.jpg.*.part").Any(),
            "successful commit must remove the partial file");
        Check(await receiver.FinishAsync(CancellationToken.None) == target,
            "repeated finish must be idempotent");
    }

    private async Task IsolatesConcurrentSameNameTransfers(string root)
    {
        var firstData = CreateBytes(70_001, 1);
        var secondData = CreateBytes(70_002, 2);
        var firstOffer = Offer("same-name.bin", firstData);
        var secondOffer = Offer("same-name.bin", secondData);
        await using var first = new TransferReceiver(root, firstOffer);
        await using var second = new TransferReceiver(root, secondOffer);
        await WriteExtents(first, firstOffer, firstData);
        await WriteExtents(second, secondOffer, secondData);

        Check(Directory.EnumerateFiles(root, "same-name.bin.*.part").Count() == 2,
            "same-name transfers must use different partial files");
        await first.FinishAsync(CancellationToken.None);
        var target = await second.FinishAsync(CancellationToken.None);
        Check(File.ReadAllBytes(target).SequenceEqual(secondData),
            "later same-name commit must atomically replace the target");
    }

    private async Task PreservesAndRetriesACommitBlockedByAnotherHandle(string root)
    {
        var oldData = CreateBytes(128, 3);
        var newData = CreateBytes(65_537, 4);
        var target = Path.Combine(root, "temporarily-locked.bin");
        await File.WriteAllBytesAsync(target, oldData);
        var offer = Offer(Path.GetFileName(target), newData);
        await using var receiver = new TransferReceiver(root, offer);
        await WriteExtents(receiver, offer, newData);

        IOException? commitFailure = null;
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await receiver.FinishAsync(CancellationToken.None); }
            catch (IOException failure) { commitFailure = failure; }
        }

        Check(commitFailure?.Message.Contains("after 4 attempts", StringComparison.Ordinal) == true,
            "persistent sharing violation must report bounded commit retries");
        Check(File.ReadAllBytes(target).SequenceEqual(oldData),
            "failed commit must not corrupt the existing target");
        Check(Directory.EnumerateFiles(root, "temporarily-locked.bin.*.part").SingleOrDefault() is not null,
            "failed commit must retain the verified partial file");

        await receiver.FinishAsync(CancellationToken.None);
        Check(File.ReadAllBytes(target).SequenceEqual(newData),
            "finish must be retryable after the external lock is released");
        Check(!Directory.EnumerateFiles(root, "temporarily-locked.bin.*.part").Any(),
            "successful retry must consume the partial file");
    }

    private async Task ResumesAfterReceiverRestart(string root)
    {
        var data = CreateBytes(160_321, 5);
        var offer = Offer("restart-resume.bin", data);
        await using (var first = new TransferReceiver(root, offer))
        {
            var firstExtent = data.AsSpan(0, offer.ExtentSize).ToArray();
            await first.AcceptAsync(new FileExtent(offer.Id, 0, SHA256.HashData(firstExtent), firstExtent),
                CancellationToken.None);
            Check(first.ContiguousBytes == offer.ExtentSize,
                "receiver must expose the committed resume offset before restart");
        }

        await using (var resumed = new TransferReceiver(root, offer))
        {
            Check(resumed.ContiguousBytes == offer.ExtentSize,
                "receiver must restore committed extents after process restart");
            for (var offset = offer.ExtentSize; offset < data.Length; offset += offer.ExtentSize)
            {
                var index = offset / offer.ExtentSize;
                var length = Math.Min(offer.ExtentSize, data.Length - offset);
                var extent = data.AsSpan(offset, length).ToArray();
                await resumed.AcceptAsync(new FileExtent(offer.Id, index, SHA256.HashData(extent), extent),
                    CancellationToken.None);
            }
            var target = await resumed.FinishAsync(CancellationToken.None);
            Check(File.ReadAllBytes(target).SequenceEqual(data),
                "resumed transfer must commit the exact original bytes");
            Check(!File.Exists($"{target}.{offer.Id:N}.part.resume"),
                "successful resume must remove durable metadata");
        }
    }

    private static FileOffer Offer(string name, byte[] data, int extentSize = 65_536) =>
        new(Guid.NewGuid(), name, data.LongLength, extentSize, SHA256.HashData(data));

    private static async Task WriteExtents(TransferReceiver receiver, FileOffer offer, byte[] data)
    {
        var index = 0;
        for (var offset = 0; offset < data.Length; offset += offer.ExtentSize)
        {
            var length = Math.Min(offer.ExtentSize, data.Length - offset);
            var extent = data.AsSpan(offset, length).ToArray();
            await receiver.AcceptAsync(new FileExtent(offer.Id, index++, SHA256.HashData(extent), extent),
                CancellationToken.None);
        }
    }

    private static byte[] CreateBytes(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
