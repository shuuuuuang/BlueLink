using System.IO;
using System.Security.Cryptography;
using BlueLink.Usb;
using BlueLink.Transfer;

internal static class MtpVerification
{
    public static async Task RunAsync()
    {
        var id = Guid.Parse("12345678-1234-5678-9abc-123456789abc");
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        foreach (var size in new[] { 0, 1, 17, 65537, 1048576, 1048593, 2097153 })
        {
            var source = Enumerable.Range(0, size).Select(i => (byte)(i * 31 + 7)).ToArray();
            using var encoded = new MemoryStream();
            await MtpFileCipher.EncryptAsync(new MemoryStream(source), encoded, id, size, key);
            Require(encoded.Length == MtpFileCipher.EncodedSize(size), "encoded length");
            var encrypted = encoded.ToArray();
            Console.WriteLine($"BLM1 {size} {Convert.ToHexString(SHA256.HashData(encrypted))}");
            using var decoded = new MemoryStream();
            await MtpFileCipher.DecryptAsync(new MemoryStream(encrypted), id, size, key, bytes => decoded.WriteAsync(bytes).AsTask());
            Require(decoded.ToArray().SequenceEqual(source), "cipher roundtrip");
            await Reject(encrypted[..^1], id, size, key);
            var damaged = (byte[])encrypted.Clone(); damaged[^1] ^= 1;
            await Reject(damaged, id, size, key);
            await Reject(encrypted, Guid.NewGuid(), size, key);
            await Reject([.. encrypted, 1], id, size, key);
        }
        foreach (var invalid in new[] { -1L, long.MaxValue })
        {
            try { MtpFileCipher.EncodedSize(invalid); throw new Exception("Invalid size accepted"); }
            catch (InvalidDataException) { }
        }
        var root = Path.Combine(Path.GetTempPath(), "bluelink-mtp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = RandomNumberGenerator.GetBytes(MtpFileCipher.ChunkSize + 17);
            var offer = new FileOffer(Guid.NewGuid(), "roundtrip.bin", source.Length, 65536, SHA256.HashData(source));
            await using (var receiver = new TransferReceiver(root, offer))
            {
                await receiver.ImportChunkAsync(0, source.AsMemory(0, MtpFileCipher.ChunkSize), default);
                Require(receiver.ContiguousBytes == MtpFileCipher.ChunkSize, "import checkpoint");
            }
            await using (var receiver = new TransferReceiver(root, offer))
            {
                Require(receiver.ContiguousBytes == MtpFileCipher.ChunkSize, "import checkpoint survives reopen");
                await receiver.ImportChunkAsync(MtpFileCipher.ChunkSize, source.AsMemory(MtpFileCipher.ChunkSize), default);
                var path = await receiver.FinishAsync(default);
                Require(File.ReadAllBytes(path).SequenceEqual(source), "import final bytes");
            }
        }
        finally { foreach (var file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
        var queue = new DeviceFileQueue();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var order = new List<int>();
        var first = queue.RunAsync(async _ => { order.Add(1); entered.SetResult(); await release.Task; }, default);
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var second = queue.RunAsync(_ => { order.Add(2); return Task.CompletedTask; }, cancel.Token);
        var third = queue.RunAsync(_ => { order.Add(3); return Task.CompletedTask; }, default);
        cancel.Cancel();
        try { await second; throw new Exception("Canceled queue ran"); } catch (OperationCanceledException) { }
        Require(!third.IsCompleted && order.SequenceEqual(new[] { 1 }), "queued file overtook paused head");
        var independent = new DeviceFileQueue();
        await independent.RunAsync(_ => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(1));
        release.SetResult(); await Task.WhenAll(first, third);
        Require(order.SequenceEqual(new[] { 1, 3 }) && queue.Count == 0, "FIFO cancellation recovery");
        try { await queue.RunAsync(_ => Task.FromException(new IOException("synthetic")), default); } catch (IOException) { }
        await queue.RunAsync(_ => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(1));
        var devices = await WpdFileHost.Worker(WpdDevice.Devices, default);
        Console.WriteLine($"WPD enumeration: {devices.Length} USB device(s)");
        Console.WriteLine("MTP cipher, tamper, queue, cross-device isolation and COM enumeration passed");
    }
    private static async Task Reject(byte[] bytes, Guid id, long size, byte[] key)
    {
        try { await MtpFileCipher.DecryptAsync(new MemoryStream(bytes), id, size, key, _ => Task.CompletedTask); }
        catch (Exception error) when (error is CryptographicException or IOException or InvalidDataException) { return; }
        throw new Exception("Corrupt USB file accepted");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
