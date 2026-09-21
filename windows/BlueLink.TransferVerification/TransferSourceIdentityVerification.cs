using System.IO;
using System.Security.Cryptography;
using BlueLink.Domain;
using BlueLink.Session;

internal static class TransferSourceIdentityVerification
{
    public static async Task RunAsync(string output)
    {
        var root = Path.Combine(Path.GetFullPath(output), "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "qa-source.bin");
        await File.WriteAllTextAsync(file, "file version A");
        var original = await File.ReadAllBytesAsync(file);
        var value = new TransferItem { Id = Guid.NewGuid(), Name = "qa-source.bin", TotalBytes = original.Length,
            Outgoing = true, Status = TransferStatus.Failed, SourceSha256 = Convert.ToHexString(SHA256.HashData(original)) };
        var checks = 0;
        async Task Verify(bool expected)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var accepted = true;
            try { TransferSourceIdentity.Validate(value, bytes.Length, SHA256.HashData(bytes)); }
            catch (InvalidOperationException) { accepted = false; }
            if (accepted != expected) throw new Exception("Source identity check accepted changed or missing identity");
            checks++;
        }
        await Verify(true);
        value.SourceSha256 = value.SourceSha256.ToLowerInvariant(); await Verify(true);
        await File.WriteAllTextAsync(file, "file version B"); await Verify(false);
        await File.WriteAllTextAsync(file, "file version A longer"); await Verify(false);
        await File.WriteAllBytesAsync(file, original); await Verify(true);
        foreach (var invalid in new string?[] { null, "", "00", new string('x', 64) })
        { value.SourceSha256 = invalid; await Verify(false); }
        Console.WriteLine($"Source identity: {checks} checks passed; same-size replacement, size change, legacy and malformed identity rejected.");
    }
}
