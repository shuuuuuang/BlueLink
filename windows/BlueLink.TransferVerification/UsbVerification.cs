using System.IO;
using System.Threading.Channels;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;
using BlueLink.Usb;

internal sealed partial class UsbVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlueLinkUsbVerification-" + Guid.NewGuid().ToString("N")));
        var logging = SessionLog.Enabled; SessionLog.Enabled = false;
        try
        {
            var osGuid = new Guid("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");
            var infGuid = Guid.NewGuid();
            Check(WindowsUsbInventory.ParseInterfaceGuids(null, osGuid.ToString()).SequenceEqual(new[] { osGuid }),
                "firmware singular DeviceInterfaceGUID registration is accepted");
            Check(WindowsUsbInventory.ParseInterfaceGuids(new[] { osGuid.ToString(), "invalid", infGuid.ToString() },
                    osGuid.ToString().ToUpperInvariant(), 42).SequenceEqual(new[] { osGuid, infGuid }),
                "INF multi-string and firmware GUID registrations merge without invalid or duplicate entries");
            await TransferPauseVerification.RunAsync();
            await VerifyAoa();
            await VerifyHost();
            await VerifyConcurrentHosts();
            VerifyReadiness();
            await VerifyRouting(root);
            await VerifyUnrestrictedPeerCount(root);
            await VerifyLiveness(root);
            Check(!new UsbSnapshot(UsbStage.Waiting).Channel.Contains("Bluetooth"), "no Bluetooth fallback is advertised without an actual peer session");
            Check(new UsbSnapshot(UsbStage.HighSpeed, Speed: UsbLinkSpeed.Unknown).Channel == "USB", "unknown physical speed is not fabricated as High-Speed");
            Check(AoaProtocol.IsAccessoryDataInterface("USB\\VID_18D1&PID_2D01&MI_00", "USB\\Class_FF&SubClass_FF&Prot_00"), "AOA data interface accepted");
            Check(!AoaProtocol.IsAccessoryDataInterface("USB\\VID_18D1&PID_2D01&MI_01", "USB\\Class_FF&SubClass_42&Prot_01"), "ADB side interface excluded from accessory data");
            Check(!AoaProtocol.IsAndroidCandidate("USB\\VID_1234&PID_5678", "USB\\Class_08"), "unrelated USB storage is not a negotiation candidate");
            Check(!AoaProtocol.IsAndroidCandidate("USB\\VID_18D1&PID_2D01", "USB\\DevClass_00;USB\\COMPOSITE"),
                "composite parent is excluded; only its AOA data child is opened");
            Check(AoaProtocol.IsAccessoryDataInterface("USB\\VID_18D1&PID_2D00", "USB\\Class_FF&SubClass_FF&Prot_00"),
                "single-interface accessory remains a data candidate");
            Console.WriteLine($"BlueLink USB and transport verification passed: {_checks} checks");
        }
        finally
        {
            SessionLog.Enabled = logging;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("BlueLinkUsbVerification-", StringComparison.Ordinal))
                throw new IOException("Invalid USB verification cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private async Task VerifyAoa()
    {
        foreach (var protocol in new byte[] { 1, 2 })
        {
            var pipe = new ControlFixture(protocol);
            await AoaProtocol.StartAccessoryAsync(pipe, "qa-host", CancellationToken.None);
            Check(pipe.Requests.Count == 8 && pipe.Requests[0].Request == 51 && pipe.Requests[0].Type == 0xC0 &&
                  pipe.Requests[^1].Request == 53 && pipe.Requests[^1].Type == 0x40, "AOA queries support before identification and re-enumeration");
            Check(pipe.Requests.Skip(1).Take(6).Select(value => value.Index).SequenceEqual(new ushort[] { 0, 1, 2, 3, 4, 5 }) &&
                  pipe.Requests.Skip(1).Take(6).All(value => value.Bytes.Length <= 256 && value.Bytes[^1] == 0), "all six AOA identifiers are indexed and null terminated");
            Check(System.Text.Encoding.UTF8.GetString(pipe.Requests[4].Bytes).TrimEnd('\0') == "1.0", "mandatory version is supplied for older Android compatibility");
        }
        var unsupported = new ControlFixture(0);
        try { await AoaProtocol.StartAccessoryAsync(unsupported, "qa", CancellationToken.None); throw new Exception("Expected unsupported AOA"); }
        catch (UsbFailure error) { Check(error.Stage == UsbStage.Unsupported && unsupported.Requests.Count == 1, "unsupported device receives no mode-changing request"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = new ControlFixture(1);
        try { await AoaProtocol.StartAccessoryAsync(canceled, "qa", cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Check(canceled.Requests.Count == 0, "canceled negotiation performs no control transfer"); }
    }

    private async Task VerifyHost()
    {
        var backend = new HostFixture();
        await using var host = new UsbHost(backend, () => "qa", _ => Task.CompletedTask, _ => false);
        await host.SetEnabledAsync(false);
        Check(backend.Enumerations == 0, "disabled USB performs no native enumeration");
        foreach (var policy in new[] { false, true })
        {
            backend.Device = new("qa-device", "QA phone", "", "", true, true, policy ? 52u : 28u, policy);
            var changed = new TaskCompletionSource<UsbSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = policy ? UsbStage.PolicyBlocked : UsbStage.DriverMissing;
            void Observe(UsbSnapshot value) { if (value.Stage == expected) changed.TrySetResult(value); }
            host.Changed += Observe;
            await host.SetEnabledAsync(true);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(backend.Opens == 0, "driver or policy restriction never opens or reconfigures the device");
            await host.SetEnabledAsync(false); host.Changed -= Observe;
        }
    }

    private async Task VerifyRouting(string root)
    {
        var a = new IdentityStore(Path.Combine(root, "a")); var b = new IdentityStore(Path.Combine(root, "b"));
        await using var first = new SessionSupervisor(a, request => request.Confirm());
        await using var second = new SessionSupervisor(b, request => request.Confirm());
        first.OutgoingDirectory = Path.Combine(root, "outgoing"); second.ReceiveDirectory = Path.Combine(root, "received");
        var messages = Channel.CreateUnbounded<(SessionSnapshot Session, ChatItem Message)>();
        second.MessageReceived += (session, message) => messages.Writer.TryWrite((session, message));
        var held = await Connect(first, second, TransportKind.Bluetooth, hold: true);
        var initialA = await WaitFor(first, TransportKind.Bluetooth);
        var initialB = await WaitFor(second, TransportKind.Bluetooth);
        await first.SendChatAsync(initialA.SessionId, "蓝牙原文");
        var received = await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Check(received.Message.Text == "蓝牙原文" && received.Session.Transport == TransportKind.Bluetooth, "real encrypted chat uses the initial Bluetooth fixture");
        var offered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource<TransportKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingReceiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<(string Path, TransportKind Transport)>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.TransferChanged += (session, item) =>
        {
            if (item.Outgoing) offered.TrySetResult(item.Id);
            if (item.Status == TransferStatus.Paused) paused.TrySetResult(session.Transport);
        };
        second.TransferChanged += (session, item) =>
        {
            if (!item.Outgoing && item.Status == TransferStatus.RemotePaused) waitingReceiver.TrySetResult();
            if (item.Status == TransferStatus.Completed && item.LocalPath is { } path) completed.TrySetResult((path, session.Transport));
        };
        var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(256 * 1024);
        var sourceFile = Path.Combine(root, "qa-file.bin"); await File.WriteAllBytesAsync(sourceFile, payload);
        var sending = first.SendFileAsync(initialA.SessionId, sourceFile);
        await held!.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Connect(first, second, TransportKind.Usb);
        var usbA = await WaitFor(first, TransportKind.Usb); var usbB = await WaitFor(second, TransportKind.Usb);
        Check(usbA.SessionId == initialA.SessionId && usbB.SessionId == initialB.SessionId && first.Snapshot().Count == 1 && second.Snapshot().Count == 1,
            "authenticated USB promotion preserves logical conversation IDs and one public session");
        Check(first.HasTransport(usbA.PeerId!, TransportKind.Bluetooth) && first.HasTransport(usbA.PeerId!, TransportKind.Usb), "Bluetooth remains as an authenticated standby");
        var pause = first.PauseTransferAsync(initialA.SessionId, await offered.Task);
        Check(await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)) == TransportKind.Bluetooth, "pause after USB promotion targets the existing Bluetooth transfer owner");
        held.Release.TrySetResult(); await pause.WaitAsync(TimeSpan.FromSeconds(5));
        await waitingReceiver.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "sender pause is propagated over the encrypted transfer's original channel to the waiting receiver");
        await first.ResumeTransferAsync(initialA.SessionId, await offered.Task);
        await sending.WaitAsync(TimeSpan.FromSeconds(12));
        var completedFile = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(completedFile.Transport == TransportKind.Bluetooth && (await File.ReadAllBytesAsync(completedFile.Path)).SequenceEqual(payload),
            "file started before promotion completes intact on its original transport");
        await first.SendChatAsync(initialA.SessionId, "USB 原文");
        received = await ReadChat(messages.Reader, "USB 原文");
        Check(received.Message.Text == "USB 原文" && received.Session.Transport == TransportKind.Bluetooth, "messages remain on authenticated Bluetooth even when a legacy USB route exists");
        first.UsbEnabled = false;
        await first.SendChatAsync(initialA.SessionId, "关闭开关后的原文");
        received = await ReadChat(messages.Reader, "关闭开关后的原文");
        Check(received.Session.Transport == TransportKind.Bluetooth, "switch off blocks new USB sends before the old USB transport is disposed");
        first.UsbEnabled = true;
        var c = new IdentityStore(Path.Combine(root, "c"));
        await using var third = new SessionSupervisor(c, request => request.Confirm());
        var thirdMessages = Channel.CreateUnbounded<(SessionSnapshot Session, ChatItem Message)>();
        third.MessageReceived += (session, message) => thirdMessages.Writer.TryWrite((session, message));
        await Connect(first, third, TransportKind.Bluetooth);
        await WaitFor(third, TransportKind.Bluetooth);
        await Until(() => first.Snapshot().Count(value => value.Phase == ConnectionPhase.Connected) == 2);
        var thirdRoute = first.Snapshot().Single(value => value.PeerId != initialA.PeerId && value.Phase == ConnectionPhase.Connected);
        await first.SendChatAsync(thirdRoute.SessionId, "另一台手机的消息");
        Check((await ReadChat(thirdMessages.Reader, "另一台手机的消息")).Session.Transport == TransportKind.Bluetooth,
            "a second authenticated Android peer has an independent Bluetooth control route");
        await second.DisconnectTransportAsync(TransportKind.Usb);
        var fallbackA = await WaitFor(first, TransportKind.Bluetooth); var fallbackB = await WaitFor(second, TransportKind.Bluetooth);
        Check(fallbackA.SessionId == initialA.SessionId && fallbackB.SessionId == initialB.SessionId, "USB disconnect restores the same logical Bluetooth conversation");
        await first.SendChatAsync(initialA.SessionId, "回退后的原文");
        received = await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Check(received.Message.Text == "回退后的原文" && received.Session.Transport == TransportKind.Bluetooth, "encrypted traffic still works after fallback");
        Check(first.HasTransport(thirdRoute.PeerId!, TransportKind.Bluetooth), "disconnecting one phone leaves the other USB channel connected");
        await first.SendChatAsync(thirdRoute.SessionId, "其他会话不受影响");
        await ReadChat(thirdMessages.Reader, "其他会话不受影响");
        a.RemoveTrust(initialA.PeerId!);
        try { await first.SendChatAsync(initialA.SessionId, "must not send"); throw new Exception("Expected revocation"); }
        catch (TrustHandshakeException error) { Check(error.Stage == TrustStage.Revoked, "revoked trust blocks application data even before transport disposal"); }
        await first.DisconnectAsync(initialA.SessionId);
        Check(first.Snapshot().Count == 1 && first.HasTransport(thirdRoute.PeerId!, TransportKind.Bluetooth), "disconnect closes only the chosen peer routes");
    }

    private static async Task<(SessionSnapshot Session, ChatItem Message)> ReadChat(ChannelReader<(SessionSnapshot Session, ChatItem Message)> reader, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var value = await reader.ReadAsync(deadline.Token); if (value.Message.Text == expected) return value; }
    }
    private static async Task<GateStream?> Connect(SessionSupervisor a, SessionSupervisor b, TransportKind kind, bool hold = false)
    {
        var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
        var gate = hold ? new GateStream(left) : null;
        await a.AddAsync(new StreamConnection((Stream?)gate ?? left, kind, false), false);
        await b.AddAsync(new StreamConnection(right, kind, true), true);
        return gate;
    }
    private static async Task<SessionSnapshot> WaitFor(SessionSupervisor supervisor, TransportKind kind)
    {
        var result = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(SessionSnapshot value) { if (value.Transport == kind && value.Phase == ConnectionPhase.Connected) result.TrySetResult(value); }
        supervisor.SessionChanged += Check;
        try { foreach (var value in supervisor.Snapshot()) Check(value); return await result.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { supervisor.SessionChanged -= Check; }
    }
    private void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException("USB verification failed: " + label); _checks++; }
    private sealed class StreamConnection(Stream duplex, TransportKind kind, bool listener) : IPeerConnection
    {
        public Stream Input => duplex;
        public Stream Output => duplex;
        public string PeerName => "QA phone";
        public string TransportAddress => kind == TransportKind.Usb ? "usb:qa" : "qa:bluetooth";
        public bool ListenerRole => listener;
        public TransportKind Transport => kind;
        public PeerPlatform Platform => PeerPlatform.Android;
        public ValueTask DisposeAsync() => duplex.DisposeAsync();
    }
    private sealed class GateStream(Stream inner) : Stream
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length > 8192 && Interlocked.Exchange(ref _held, 1) == 0)
            { Blocked.TrySetResult(); await Release.Task.WaitAsync(token); }
            await inner.WriteAsync(buffer, token);
        }
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        protected override void Dispose(bool disposing) { if (disposing) { Release.TrySetResult(); inner.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class ControlFixture(byte protocol) : IUsbControlPipe
    {
        public List<(byte Type, byte Request, ushort Index, byte[] Bytes)> Requests { get; } = [];
        public Task<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((requestType, request, index, bytes.ToArray()));
            if (request == 51) bytes[0] = protocol;
            return Task.FromResult(bytes.Length);
        }
    }
    private sealed class HostFixture : IUsbHostBackend
    {
        public int Enumerations;
        public int Opens;
        public UsbDevice? Device;
        public Task<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Enumerations);
            return Task.FromResult<IReadOnlyList<UsbDevice>>(Device is null ? [] : [Device]);
        }
        public Task<IUsbHostConnection> OpenAsync(UsbDevice device, CancellationToken token) { Interlocked.Increment(ref Opens); throw new InvalidOperationException("Unexpected device open"); }
    }
}
