using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BlueLink.Domain;
using BlueLink.Session;

// Explicit physical-test commands, confined to the selected USB peer and workspace output.
internal sealed partial class UsbHardwareVerification
{
    private void ObserveCommands(SessionSupervisor sessions)
    {
        var last = new ConcurrentDictionary<Guid, (TransferStatus Status, long At)>();
        sessions.TransferChanged += (session, item) =>
        {
            var now = Environment.TickCount64;
            if (last.TryGetValue(item.Id, out var prior) && prior.Status == item.Status && now - prior.At < 2000) return;
            last[item.Id] = (item.Status, now);
            Log("physical-transfer", new { session.SessionId, session.Transport, item.Id, item.Name,
                item.Outgoing, item.Status, item.TotalBytes, item.CompletedBytes, item.LocalPath, item.FailureDetail });
        };
        sessions.MessageReceived += (session, item) => Log("physical-message", new { session.SessionId, session.Transport, item.Id, item.Text, item.Outgoing, item.Status });
        sessions.ReceiptReceived += (session, receipt) => Log("physical-receipt", new { session.SessionId, receipt });
    }

    private async Task RunCommandsAsync(SessionSupervisor sessions, SessionSnapshot session, CancellationToken token)
    {
        var inbox = Path.Combine(_root, "commands");
        Directory.CreateDirectory(inbox);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Task>();
        Log("commands-ready", new { session.SessionId, session.PeerId, session.Transport });
        while (!File.Exists(Path.Combine(_root, "stop.txt")))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFiles(inbox, "*.json").Order())
            {
                if (!processed.Add(path)) continue;
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
                var command = document.RootElement;
                var id = Path.GetFileNameWithoutExtension(path);
                var action = command.GetProperty("action").GetString();
                try
                {
                    switch (action)
                    {
                        case "send-file":
                        {
                            var name = command.GetProperty("name").GetString()!;
                            if (!name.StartsWith("bluelink-qa-", StringComparison.Ordinal) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                                throw new ArgumentException("Only explicitly named QA files are accepted.");
                            var size = command.GetProperty("bytes").GetInt64();
                            if (size < 0 || size > 600L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
                            var source = Path.Combine(_root, name);
                            if (!File.Exists(source))
                            {
                                await using var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                var block = new byte[64 * 1024];
                                for (long written = 0; written < size;)
                                {
                                    RandomNumberGenerator.Fill(block);
                                    var count = (int)Math.Min(block.Length, size - written);
                                    await file.WriteAsync(block.AsMemory(0, count), token); written += count;
                                }
                            }
                            if (new FileInfo(source).Length != size) throw new IOException("Existing QA file has a different size.");
                            await using var input = File.OpenRead(source);
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                            Log("command-send", new { id, name, bytes = size, sha256 = hash });
                            pending.Add(CompleteSendAsync(sessions, session.SessionId, source, id, token));
                            break;
                        }
                        case "send-chat":
                            var text = command.GetProperty("text").GetString()!;
                            if (!text.StartsWith("BlueLink QA ", StringComparison.Ordinal)) throw new ArgumentException("QA message prefix required.");
                            await sessions.SendChatAsync(session.SessionId, text).WaitAsync(token);
                            Log("command-complete", new { id, action });
                            break;
                        case "cancel":
                            await sessions.CancelTransferAsync(session.SessionId, command.GetProperty("transferId").GetGuid());
                            Log("command-complete", new { id, action });
                            break;
                        case "pause":
                            await sessions.PauseTransferAsync(session.SessionId, command.GetProperty("transferId").GetGuid());
                            Log("command-complete", new { id, action });
                            break;
                        case "resume":
                            await sessions.ResumeTransferAsync(session.SessionId, command.GetProperty("transferId").GetGuid());
                            Log("command-complete", new { id, action });
                            break;
                        default: throw new ArgumentException("Unsupported physical test command.");
                    }
                }
                catch (Exception error) { Log("command-failed", new { id, action, error = error.Message }); }
            }
            await Task.Delay(200, token);
        }
        await sessions.DisconnectTransportAsync(BlueLink.Transport.TransportKind.Usb);
        await Task.WhenAll(pending);
    }

    private async Task CompleteSendAsync(SessionSupervisor sessions, Guid sessionId, string source, string id, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await sessions.SendFileAsync(sessionId, source).WaitAsync(token);
            // Consumers also check the matching physical-transfer Completed event and destination hash.
            Log("command-complete", new { id, action = "send-file", seconds = watch.Elapsed.TotalSeconds, sendTaskCompleted = true });
        }
        catch (Exception error) { Log("command-failed", new { id, action = "send-file", error = error.Message, seconds = watch.Elapsed.TotalSeconds }); }
    }
}
