using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BlueLink.Feedback;
using BlueLink.Storage;

internal sealed class FeedbackVerification
{
    private int _checks;

    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BlueLinkFeedbackVerification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Check(new FeedbackDraft("连接问题", " \r\n", false).ValidationError is not null, "description required");
            Check(new FeedbackDraft("unknown", "问题", false).ValidationError is not null, "category validated");
            var boundary = new FeedbackDraft("连接问题", new string('测', 1000), false);
            Check(boundary.ValidationError is null, "1000 characters accepted");
            Check((boundary with { Description = new string('测', 1001) }).ValidationError is not null, "1001 characters rejected");
            var log = Path.Combine(root, "session.log");
            await File.WriteAllTextAsync(log,
                "[2026-09-06T08:00:00.0000000+08:00] Transfer | 文件发送失败，id=123456，name=private.txt | IOException (0x80070020): SECRET C:\\Users\\Person\\private.txt\n" +
                "private stack and arbitrary chat content\n" +
                "[2026-09-06T08:01:00.0000000+08:00] Session | 会话开始，peer=Secret Device，role=dialer\n");
            var off = Path.Combine(root, "off.zip");
            using (var lockedLog = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await FeedbackPackageService.GenerateAsync(boundary, off, log);
            using (var zip = ZipFile.OpenRead(off))
            {
                Check(zip.Entries.Count == 2 && zip.GetEntry("diagnostics.log") is null, "diagnostics off never reads locked source or adds log");
                using var json = JsonDocument.Parse(await ReadEntryAsync(zip, "feedback.json"));
                Check(json.RootElement.GetProperty("description").GetString()!.Length == 1000, "full boundary description survives zip");
                Check(!json.RootElement.GetProperty("includesDiagnostics").GetBoolean(), "metadata matches diagnostics off");
            }
            var on = Path.Combine(root, "on.zip");
            await FeedbackPackageService.GenerateAsync(boundary with { IncludeDiagnostics = true }, on, log);
            using (var zip = ZipFile.OpenRead(on))
            {
                Check(zip.Entries.Count == 3, "diagnostics on includes only three expected files");
                var text = await ReadEntryAsync(zip, "diagnostics.log");
                Check(text.Contains("文件发送失败") && text.Contains("IOException (0x80070020)"), "event and error type preserved");
                Check(!text.Contains("SECRET") && !text.Contains("private") && !text.Contains("Secret Device") &&
                    !text.Contains("123456") && !text.Contains("Person"), "identity, paths, names and arbitrary text excluded");
            }
            var original = await File.ReadAllBytesAsync(off);
            await ExpectFailureAsync<IOException>(() => FeedbackPackageService.GenerateAsync(boundary, off, log));
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(off)), "existing package preserved without overwrite approval");
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "failed publish cleans only own temporary file");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await ExpectFailureAsync<OperationCanceledException>(() => FeedbackPackageService.GenerateAsync(
                    boundary, Path.Combine(root, "cancel.zip"), log, token: cancel.Token));
                Check(!File.Exists(Path.Combine(root, "cancel.zip")), "cancelled work creates no package");
            }
            using (var lockedLog = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await ExpectFailureAsync<IOException>(() => FeedbackPackageService.GenerateAsync(
                    boundary with { IncludeDiagnostics = true }, off, log, overwrite: true));
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(off)), "diagnostic read failure preserves previous package");
            Check(boundary.Description.Length == 1000, "failure does not mutate caller draft");
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "diagnostic failure removes incomplete temporary zip");

            await File.WriteAllBytesAsync(log, Encoding.UTF8.GetBytes(new string('x', DiagnosticsSnapshot.MaximumSourceBytes + 100) +
                "\n[2026-09-06T08:02:00.0000000+08:00] Transfer | 文件发送失败，name=hidden\n"));
            var tail = await DiagnosticsSnapshot.ReadAsync(log);
            Check(tail.Contains("1 MiB") && tail.Contains("文件发送失败") && tail.Length < 300, "oversize log reads bounded tail and discards partial first record");
            var missing = await DiagnosticsSnapshot.ReadAsync(Path.Combine(root, "missing.log"));
            Check(missing.Contains("尚未生成"), "missing diagnostic log has honest empty state");
            var usage = StorageUsage.Measure(root);
            Check(usage.Bytes > DiagnosticsSnapshot.MaximumSourceBytes, "storage estimate measures real files");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { StorageUsage.Measure(root, cancel.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { _checks++; }
            }
            Console.WriteLine($"BlueLink feedback verification passed: {_checks} checks");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("BlueLinkFeedbackVerification-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected verification directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private async Task ExpectFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }

    private void Check(bool value, string name)
    {
        if (!value) throw new Exception($"Feedback verification failed: {name}");
        _checks++;
    }
}
