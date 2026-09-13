using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Usb;
using BlueLink.Transport;

// Explicit opt-in physical test. Uses the production WinUSB, BTX handshake and file protocol.
internal sealed partial class UsbHardwareVerification
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _logGate = new();
    private string _root = "";
    private void Log(string kind, object value)
    {
        lock (_logGate)
        {
            var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, value },
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            File.AppendAllText(Path.Combine(_root, "events.jsonl"), line + Environment.NewLine);
            Console.WriteLine(line);
        }
    }
    public async Task RunAsync(string target, string output, bool sessionOnly = false, bool commands = false, string? identityDirectory = null)
    {
        _root = Path.GetFullPath(output);
        var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".acceptance")) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Hardware output must be inside workspace .acceptance.");
        Directory.CreateDirectory(_root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
        var token = deadline.Token;
        SessionLog.DirectoryPath = Path.Combine(_root, "logs");
        var identityRoot = Path.GetFullPath(identityDirectory ?? Path.Combine(_root, "identity"));
        if (!identityRoot.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Hardware test identity must stay inside workspace .acceptance.");
        var identity = new IdentityStore(identityRoot);
        Log("host-identity", new { peerId = Convert.ToHexString(identity.Identity.PeerId) });
        var confirmations = new ConcurrentBag<Task>();
        void PresentTrust(TrustRequest request)
        {
            var challenge = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(_root, "trust-" + challenge + ".json"), JsonSerializer.Serialize(new {
                challenge, request.SafetyCode, request.LocalFingerprint, request.RemoteFingerprint, request.PeerName
            }, _json));
            Log("trust", new { challenge, request.SafetyCode, request.Stage });
            confirmations.Add(Task.Run(async () =>
            {
                var confirm = Path.Combine(_root, "confirm-" + challenge + ".txt");
                try
                {
                    while (request.Stage == TrustStage.Confirm)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(confirm))
                        {
                            if ((await File.ReadAllTextAsync(confirm, token)).Trim() != request.SafetyCode)
                                throw new InvalidOperationException("Safety code confirmation does not match current challenge.");
                            request.Confirm(); Log("confirmed", new { challenge }); return;
                        }
                        await Task.Delay(200, token);
                    }
                }
                catch { request.Cancel(); throw; }
            }, token));
        }
        await using var sessions = new SessionSupervisor(identity, PresentTrust)
        {
            OutgoingDirectory = Path.Combine(_root, "outgoing"), ReceiveDirectory = Path.Combine(_root, "received"),
            AutoAcceptFiles = true, MaxReceiveBytes = 500L * 1024 * 1024
        };
        var ready = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (commands) ObserveCommands(sessions);
        await using var host = new UsbHost(new ExactDeviceBackend(target), () => "BlueLink-USB-benchmark",
            async connection => { await sessions.AddAsync(connection, connection.ListenerRole); }, _ => false);
        host.Changed += value =>
        {
            Log("usb", value);
            if (value.Stage is UsbStage.DriverMissing or UsbStage.PolicyBlocked or UsbStage.PermissionDenied or UsbStage.Unsupported or UsbStage.Unavailable)
                ready.TrySetException(new IOException("USB unavailable: " + value.Stage + " " + value.Error));
        };
        sessions.SessionChanged += value =>
        {
            Log("session", value); host.ObserveSession(value);
            if (value.Phase == ConnectionPhase.Connected && value.Transport == TransportKind.Usb) ready.TrySetResult(value);
            else if (value.Phase == ConnectionPhase.Disconnected) ready.TrySetException(new IOException(value.Detail));
        };
        string currentName = "";
        TransferItem? latest = null;
        long lastLog = 0;
        TransferStatus? lastStatus = null;
        sessions.TransferChanged += (session, item) =>
        {
            if (!item.Outgoing || item.Name != currentName) return;
            if (session.Transport != TransportKind.Usb) throw new InvalidOperationException("Benchmark must stay on USB.");
            latest = item;
            var now = Environment.TickCount64;
            if (item.Status != lastStatus || now - lastLog > 2000)
            {
                lastStatus = item.Status; lastLog = now;
                Log("transfer", new { item.Id, item.Name, item.Status, item.TotalBytes, item.CompletedBytes, item.FailureDetail, session.Transport });
            }
        };
        try
        {
            await host.SetEnabledAsync(true);
            var session = await ready.Task.WaitAsync(TimeSpan.FromMinutes(3), token);
            await Task.WhenAll(confirmations);
            if (commands) { await RunCommandsAsync(sessions, session, token); return; }
            var results = new List<object>();
            foreach (var sizeMiB in sessionOnly ? Array.Empty<int>() : new[] { 16, 256 })
            {
                currentName = $"bluelink-usb-qa-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sizeMiB}MiB.bin";
                var path = Path.Combine(_root, currentName);
                var block = new byte[1024 * 1024];
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    for (var index = 0; index < sizeMiB; index++) { RandomNumberGenerator.Fill(block); await file.WriteAsync(block, token); }
                string hash;
                await using (var file = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                latest = null; lastStatus = null;
                var watch = Stopwatch.StartNew();
                Log("send-start", new { file = currentName, bytes = (long)sizeMiB * 1024 * 1024, sha256 = hash, transport = "USB" });
                await sessions.SendFileAsync(session.SessionId, path).WaitAsync(TimeSpan.FromMinutes(10), token);
                watch.Stop();
                if (latest is not { Status: TransferStatus.Completed } || latest.CompletedBytes != (long)sizeMiB * 1024 * 1024)
                    throw new IOException("Missing verified-completion acknowledgment.");
                var result = new { file = currentName, transferId = latest.Id, bytes = latest.TotalBytes,
                    seconds = watch.Elapsed.TotalSeconds, MiBPerSecond = sizeMiB / watch.Elapsed.TotalSeconds,
                    MBPerSecond = latest.TotalBytes / 1_000_000d / watch.Elapsed.TotalSeconds,
                    sha256 = hash, transport = "USB", completionAck = true,
                    timing = "SendFileAsync entry through remote verified/published completion ACK; includes local snapshot/hash and receive confirmation" };
                results.Add(result); Log("result", result);
                File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(results, _json));
            }
            Log("complete", new { transfers = results.Count });
            var until = DateTime.UtcNow.AddSeconds(sessionOnly ? 180 : 90);
            while (DateTime.UtcNow < until && !File.Exists(Path.Combine(_root, "stop.txt")))
                await Task.Delay(200, token);
        }
        finally { await sessions.DisconnectTransportAsync(TransportKind.Usb); await host.SetEnabledAsync(false); }
    }
    private sealed class ExactDeviceBackend(string target) : IUsbHostBackend
    {
        private readonly WindowsUsbBackend _native = new();
        public async Task<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken token) =>
            (await _native.EnumerateAsync(token)).Where(value => value.InstanceId.Equals(target, StringComparison.OrdinalIgnoreCase) && value.AccessoryMode).ToArray();
        public Task<IUsbHostConnection> OpenAsync(UsbDevice device, CancellationToken token)
        {
            if (!device.InstanceId.Equals(target, StringComparison.OrdinalIgnoreCase) || !device.AccessoryMode)
                throw new InvalidOperationException("Only the explicitly selected accessory data interface may be opened.");
            return _native.OpenAsync(device, token);
        }
    }
}
