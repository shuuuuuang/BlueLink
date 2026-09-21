using System.IO;
using BlueLink.Storage;
using BlueLink.Security;
using BlueLink.Updates;
using System.Runtime.InteropServices;

internal static class PortableVerification
{
    public static async Task ProbeAsync()
    {
        if (!AppStoragePaths.IsPortable) throw new InvalidOperationException("Probe requires a portable marker in an isolated package directory.");
        var identity = new IdentityStore();
        var database = new BlueLinkDatabase();
        await database.InitializeAsync(identity);
        var settings = await database.LoadSettingsAsync();
        Check(database.DatabasePath.StartsWith(AppStoragePaths.ProgramDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "database stays inside portable directory");
        using var service = new UpdateService(Path.Combine(AppStoragePaths.UserDirectory, "Cache", "Updates"), new UpdateTestSource { Portable = true });
        var release = await service.CheckAsync(UpdateService.CurrentVersion, CancellationToken.None);
        Check(release is { IsPortable: true } && release.FileName.EndsWith("-Portable.zip"), "portable checks select ZIP update");
        var package = await service.DownloadAsync(release!, null, CancellationToken.None);
        Check(File.Exists(package.Path), "portable ZIP downloads inside the application");
        try { await using var lease = await service.AcquireVerifiedPackageAsync(package, CancellationToken.None); throw new Exception("Portable must not execute an installer"); }
        catch (InvalidOperationException error) when (error.Message == UpdateService.PortableUpdateNotice) { Check(true, "portable update cannot execute an installer"); }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { AppStoragePaths.ProgramDirectory, AppStoragePaths.UserDirectory, database.DatabasePath, settings.DownloadDirectory, Architecture = RuntimeInformation.ProcessArchitecture.ToString() }));
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkPortableVerification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "old_%_中文");
        var moved = Path.Combine(root, "moved_中文");
        var identity = new IdentityStore(Path.Combine(root, "identity"));
        var data = Path.Combine(original, "Data", "Database");
        var db = new BlueLinkDatabase(data, original, portable: true);
        await db.InitializeAsync(identity);
        var settings = await db.LoadSettingsAsync();
        await db.SaveSettingsAsync(settings with { DownloadDirectory = Path.Combine(original, "Download") });
        await db.UpsertPeerAsync(new("peer", "Peer", "Android", StoredTrustState.Unknown, null, 1, 1));
        await db.UpsertConversationAsync(new("chat", "peer", 1, 0));
        await db.UpsertMessageAsync(new("message", "chat", "peer", StoredMessageDirection.Incoming, StoredMessageType.File, "", "Delivered", 1, 1));
        var local = Path.Combine(original, "Download", "文件.bin");
        var preview = Path.Combine(original, "Data", "Cache", "preview.png");
        var external = original + "-other\\source.bin";
        await db.UpsertAttachmentAsync(new("attachment", "message", null, "文件.bin", "application/octet-stream", 1, null, local, preview, "Complete"));
        await db.UpsertTransferAsync(new("transfer", "peer", "message", "Incoming", "Completed", "文件.bin", "application/octet-stream", 1, 1, local, external, null, null, null, 1, 1));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!); await File.WriteAllTextAsync(local, "data");
        Directory.Move(original, moved);
        var reopened = new BlueLinkDatabase(Path.Combine(moved, "Data", "Database"), moved, portable: true);
        await reopened.InitializeAsync(identity);
        var restored = await reopened.LoadSettingsAsync();
        Check(restored.DownloadDirectory == Path.Combine(moved, "Download"), "download setting follows move");
        var attachment = (await reopened.LoadAttachmentsAsync("message")).Single();
        Check(attachment.LocalPath == Path.Combine(moved, "Download", "文件.bin") && File.Exists(attachment.LocalPath), "history references the moved file");
        Check(attachment.PreviewPath == Path.Combine(moved, "Data", "Cache", "preview.png"), "preview path follows move");
        var transfer = (await reopened.LoadTransfersAsync()).Single();
        Check(transfer.LocalPath == attachment.LocalPath && transfer.SnapshotPath == external, "internal transfer path relocates; outside prefix remains untouched");
        await reopened.InitializeAsync(identity);
        Check((await reopened.LoadTransfersAsync()).Single().LocalPath == transfer.LocalPath, "reopening is idempotent");
        var custom = Path.Combine(root, "external-download");
        await reopened.SaveSettingsAsync(restored with { DownloadDirectory = custom });
        var movedAgain = Path.Combine(root, "again"); Directory.Move(moved, movedAgain);
        var again = new BlueLinkDatabase(Path.Combine(movedAgain, "Data", "Database"), movedAgain, portable: true);
        await again.InitializeAsync(identity);
        Check((await again.LoadSettingsAsync()).DownloadDirectory == custom, "external download selection stays external");
        Check(AppStoragePaths.ResolveProgramDirectory(Path.Combine(root, "app")) == root, "installed layout resolves root");
        var namedApp = Path.Combine(root, "app"); Directory.CreateDirectory(namedApp);
        await File.WriteAllTextAsync(Path.Combine(namedApp, AppStoragePaths.PortableMarker), "portable");
        Check(AppStoragePaths.ResolveProgramDirectory(namedApp) == namedApp, "portable folder named app is not confused with installer layout");
        foreach (var (architecture, rid) in new[] { (Architecture.X86,"win-x86"), (Architecture.X64,"win-x64"), (Architecture.Arm64,"win-arm64") })
            Check(UpdateService.GetRuntimeIdentifier(architecture) == rid, "update architecture " + rid);
        try { UpdateService.GetRuntimeIdentifier(Architecture.Arm); throw new Exception("ARM32 should be rejected"); }
        catch (PlatformNotSupportedException) { Check(true, "unsupported update architecture rejected"); }
        await new UpdateVerification().RunAsync();
        Console.WriteLine("Portable relocation and architecture verification passed (12 checks plus update regression). Evidence: " + root);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Portable verification failed: " + description);
        Console.WriteLine("[PASS] " + description);
    }
}
