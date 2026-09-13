using System.IO;
using BlueLink.Domain;
using BlueLink.Legal;
using BlueLink.Security;
using BlueLink.Storage;

internal sealed class SettingsVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlueLinkSettingsVerification-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            Check(LocalDeviceName.ValidationError("   ") is not null, "blank device name rejected");
            Check(LocalDeviceName.ValidationError(new string('a', 32)) is null, "32 character name accepted");
            Check(LocalDeviceName.ValidationError(new string('a', 33)) is not null, "33 character name rejected");
            Check(LocalDeviceName.ValidationError("我的 Windows 电脑") is null, "Unicode name accepted");
            Check(LocalDeviceName.ValidationError(new string('蓝', 27)) is not null, "name stays within the existing 80 byte wire limit");
            Check(LocalDeviceName.ValidationError("name\r\nother") is not null, "control characters rejected");
            Check(LocalDeviceName.Resolve("  QA  ") == "QA", "name trimmed");
            Check(LocalDeviceName.Resolve("") == Environment.MachineName, "legacy settings use the computer name");

            var store = new IdentityStore(root);
            var originalId = store.Identity.PeerId;
            var first = DeviceIdentity.Generate();
            var second = DeviceIdentity.Generate();
            store.Trust(first.PeerId, first.PublicKey);
            store.Trust(second.PeerId, second.PublicKey);
            Check(new IdentityStore(root).TrustedIdentities.Count == 2, "trust persists across reload");
            var path = Path.Combine(root, "identity.json");
            var before = await File.ReadAllBytesAsync(path);
            using (var locked = new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                ExpectIoFailure(() => store.RemoveTrust(Convert.ToHexString(first.PeerId)));
                Check(store.MatchesTrustedKey(first.PeerId, first.PublicKey) == true,
                    "failed removal preserves in-memory trust");
                ExpectIoFailure(store.ClearTrustedIdentities);
                Check(store.TrustedIdentities.Count == 2, "failed batch clear preserves in-memory trust");
                var revision = store.RevocationVersion;
                ExpectIoFailure(store.ResetIdentity);
                Check(store.Identity.PeerId.SequenceEqual(originalId) && store.TrustedIdentities.Count == 2 && store.RevocationVersion == revision,
                    "failed identity reset preserves identity, trust and authorization revision");
                Check(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "failed trust writes preserve the identity file");
            }
            var inFlightRevision = store.RevocationVersion;
            store.RemoveTrust(Convert.ToHexString(first.PeerId).ToLowerInvariant());
            Check(!store.TryTrust(first.PeerId, first.PublicKey, inFlightRevision), "a stale confirmation cannot undo trust removal");
            Check(store.MatchesTrustedKey(first.PeerId, first.PublicKey) is null, "removal ignores peer ID case");
            Check(new IdentityStore(root).TrustedIdentities.Count == 1, "single removal persists");
            store.ClearTrustedIdentities();
            Check(new IdentityStore(root).TrustedIdentities.Count == 0, "all removals persist");
            Check(store.Identity.PeerId.SequenceEqual(originalId), "removing trust preserves local device identity");
            store.Trust(second.PeerId, second.PublicKey);
            var beforeReset = store.RevocationVersion;
            store.ResetIdentity();
            Check(!store.Identity.PeerId.SequenceEqual(originalId) && store.TrustedIdentities.Count == 0,
                "identity reset creates a fresh key and removes all trust");
            Check(new IdentityStore(root).Identity.PeerId.SequenceEqual(store.Identity.PeerId), "reset identity persists and can be unprotected");
            Check(!store.TryTrust(first.PeerId, first.PublicKey, beforeReset), "pre-reset confirmation cannot restore trust");

            var database = new BlueLinkDatabase(root, root);
            await database.InitializeAsync(store);
            var settings = await database.LoadSettingsAsync();
            Check(settings.DuplicateFilePolicy == "rename" && settings.AutoDownloadFiles && settings.AllowDiscovery && settings.ReconnectAfterDisconnect,
                "aligned defaults preserve automatic receiving and safely rename duplicate files");
            Check(BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(true, false, false, true, false) &&
                !BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(true, false, false, false, true), "initial connection uses only auto-connect preference");
            Check(BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(true, true, false, false, true) &&
                !BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(true, true, false, true, false), "an established connection uses only reconnect preference");
            Check(!BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(true, true, true, true, true) &&
                !BlueLink.Domain.AutoConnectionPolicy.ShouldConnect(false, true, false, true, true), "manual disconnect and missing trust always suppress automatic connection");
            var aligned = settings with { DuplicateFilePolicy = "ask", AllowDiscovery = false, ReconnectAfterDisconnect = false,
                MessageNotifications = false, ConnectionNotifications = false, TransferNotifications = false, Language = "zh-TW", RetentionPeriod = "7d" };
            await database.SaveSettingsAsync(aligned);
            Check(await new BlueLinkDatabase(root, root).LoadSettingsAsync() == aligned, "new preferences and Traditional Chinese survive reopening storage");
            await database.SaveSettingsAsync(settings);
            Check(!settings.UsbEnabled, "USB defaults to disabled without changing existing Bluetooth settings");
            var peerId = Convert.ToHexString(first.PeerId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await database.UpsertPeerAsync(new(peerId, "QA", "Windows", StoredTrustState.Trusted, first.PublicKey, now, now));
            await database.UpsertConversationAsync(new("qa-conversation", peerId, now, 0));
            await database.UpsertMessageAsync(new("qa-message", "qa-conversation", peerId, StoredMessageDirection.Incoming,
                StoredMessageType.Text, "保持原文", "Received", now, now));
            var receivedFile = Path.Combine(root, "received-file.txt");
            await File.WriteAllTextAsync(receivedFile, "user content");
            await database.ClearPeerTrustAsync();
            Check((await database.LoadPeersAsync()).All(peer => peer.TrustState == StoredTrustState.Unknown && peer.IdentityPublicKey is null),
                "trust reset clears persisted peer permissions");
            Check((await database.LoadMessagesAsync("qa-conversation")).Single().Content == "保持原文" &&
                  await File.ReadAllTextAsync(receivedFile) == "user content" && await database.LoadSettingsAsync() == settings,
                "trust reset preserves chat content, received files and settings");
            store.Trust(first.PeerId, first.PublicKey);
            await database.InitializeAsync(store);
            store.RemoveTrust(peerId);
            // Simulate shutdown after the authoritative identity file changed but before SQLite was updated.
            await database.InitializeAsync(store);
            Check((await database.LoadPeersAsync()).Single().TrustState == StoredTrustState.Unknown &&
                  (await database.LoadMessagesAsync("qa-conversation")).Count == 1,
                "startup reconciles revoked trust after an interrupted database update without deleting history");
            store.Trust(first.PeerId, first.PublicKey);
            await database.InitializeAsync(store);
            await database.RevokePeerTrustAsync(peerId.ToLowerInvariant());
            Check((await database.LoadPeersAsync()).Single().IdentityPublicKey is null &&
                  (await database.LoadPeersAsync()).Single().TrustState == StoredTrustState.Unknown,
                "single-peer revocation clears both key and permission regardless of peer ID case");
            Check(settings.LocalDeviceName == "", "existing database gains an empty local-name default");
            var addresses = (await database.LoadPeersAsync()).Single() with { TransportAddress = "bluetooth-original", UsbTransportAddress = "usb-interface" };
            await database.UpsertPeerAsync(addresses);
            var reloadedPeer = (await new BlueLinkDatabase(root, root).LoadPeersAsync()).Single();
            Check(reloadedPeer.TransportAddress == "bluetooth-original" && reloadedPeer.UsbTransportAddress == "usb-interface",
                "USB address persists separately from the fallback Bluetooth address");
            Check(settings.Theme == "system" && settings.Language == "zh-CN", "legacy settings default to system theme and Chinese");
            var updated = settings with { LocalDeviceName = "QA 我的电脑", Theme = "dark", Language = "en-US", UsbEnabled = true };
            await database.SaveSettingsAsync(updated);
            Check(await new BlueLinkDatabase(root, root).LoadSettingsAsync() == updated,
                "renaming persists and preserves all other settings");
            await database.SaveSettingsAsync(updated with { Theme = "invalid", Language = "invalid" });
            var normalized = await database.LoadSettingsAsync();
            Check(normalized.Theme == "system" && normalized.Language == "zh-CN", "unknown appearance values fall back safely");
            Check(BlueLink.Localization.Strings.EnglishCatalog.All(entry =>
                System.Text.RegularExpressions.Regex.Matches(entry.Key, @"\{(\d+)").Select(match => match.Groups[1].Value).Order().SequenceEqual(
                    System.Text.RegularExpressions.Regex.Matches(entry.Value, @"\{(\d+)").Select(match => match.Groups[1].Value).Order())),
                "English translations preserve formatted argument indexes");
            await database.UpsertConversationAsync(new("qa-conversation", peerId, now, 7));
            await database.ClearConversationMessagesAsync("qa-conversation");
            Check((await database.LoadConversationsAsync()).Single().UnreadCount == 0,
                "clearing a conversation also clears its persisted unread badge");
            await database.UpsertConversationAsync(new("qa-conversation", peerId, now, 5));
            await database.ClearMessagesAsync();
            Check((await database.LoadConversationsAsync()).Single().UnreadCount == 0 && File.Exists(receivedFile),
                "clearing all chat history resets unread counts while preserving received files");
            foreach (var license in LicenseCatalog.All)
                Check(license.Text.Length > 500 && license.Text.Contains("Copyright", StringComparison.OrdinalIgnoreCase),
                    $"complete license embedded: {license.Name}");
            var displayCache = Path.Combine(root, "Cache", "Thumbnails");
            Directory.CreateDirectory(displayCache);
            var originalImage = Path.Combine(root, "original.png");
            await File.WriteAllTextAsync(originalImage, "original image sentinel");
            var receivedPreview = Path.Combine(root, "Cache", "preview.png");
            await File.WriteAllTextAsync(receivedPreview, "received preview sentinel");
            var decoding = 0;
            System.Windows.Media.Imaging.BitmapSource Decode()
            {
                decoding++;
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
                bitmap.Freeze(); return bitmap;
            }
            BlueLink.Files.DisplayThumbnailCache.Load(displayCache, originalImage, 1, Decode);
            BlueLink.Files.DisplayThumbnailCache.Load(displayCache, originalImage, 1, Decode);
            Check(decoding == 1, "display thumbnails reuse a regenerable disk cache");
            await File.WriteAllTextAsync(Path.Combine(displayCache, "in-progress.tmp"), "keep temporary work");
            BlueLink.Files.DisplayThumbnailCache.Clear(displayCache);
            Check(Directory.GetFiles(displayCache, "*.png").Length == 0 && File.Exists(Path.Combine(displayCache, "in-progress.tmp")) &&
                  await File.ReadAllTextAsync(originalImage) == "original image sentinel" &&
                  await File.ReadAllTextAsync(receivedPreview) == "received preview sentinel", "cache clearing preserves originals, received previews and temporary work");
            BlueLink.Files.DisplayThumbnailCache.Load(displayCache, originalImage, 1, Decode);
            Check(decoding == 2, "cleared display cache regenerates from the original");
            ExpectIoFailure(() => BlueLink.Files.DisplayThumbnailCache.Clear(root));
            Console.WriteLine($"BlueLink settings verification passed: {_checks} checks");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("BlueLinkSettingsVerification-", StringComparison.Ordinal))
                throw new IOException("Invalid verification cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectIoFailure(Action operation)
    {
        try { operation(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected an I/O failure.");
    }
    private void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException("Settings verification failed: " + label);
        _checks++;
    }
}
