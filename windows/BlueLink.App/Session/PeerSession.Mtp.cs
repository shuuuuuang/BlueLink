using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Transfer;
using BlueLink.Usb;

namespace BlueLink.Session;

public sealed partial class PeerSession
{
    private readonly MtpProbeSignal _mtpProbe = new();
    private long _mtpProbeStarted;
    private int _mtpProbeAttempts;
    private string? _mtpPendingReason;
    private volatile bool _mtpEnabled;
    private WpdBinding? _wpd;
    private MtpPacket? _mtpAnnouncement;
    private Task? _mtpPump;
    private readonly ConcurrentDictionary<Guid, MtpJob> _mtpJobs = new();
    private static readonly JsonSerializerOptions MtpJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public bool MtpEnabled { get => _mtpEnabled; set { if (_mtpEnabled == value) return; _mtpEnabled = value; RequestMtpProbe(); } }
    public void RequestMtpProbe() => _mtpProbe.Request();
    public bool MtpReady => _mtpEnabled && _wpd is not null;
    public Action<bool>? MtpAvailabilityChanged { get; set; }

    private void StartMtp()
    {
        if (_connection.Transport != Transport.TransportKind.Bluetooth || !_negotiation.Supports(BtxCapability.MtpFiles)) return;
        _mtpPump = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    if (!_mtpEnabled)
                    {
                        if (_wpd is not null) { DropMtp(); await SendMtp(new("disabled")); }
                    }
                    else
                    {
                        await SendMtp(new("hello"));
                        var announcement = _mtpAnnouncement;
                        if (_wpd is { } existing && !await WpdFileHost.PresentAsync(existing.DeviceId, _cancellation.Token)) DropMtp();
                        if (_wpd is null && announcement?.Path is { } path && announcement.Proof is { } proof)
                        {
                            if (_mtpProbeStarted == 0) _mtpProbeStarted = Environment.TickCount64;
                            _mtpProbeAttempts++;
                            var found = await WpdFileHost.BindAsync(path, Convert.FromBase64String(proof), _cancellation.Token, reason =>
                            {
                                if (reason.Contains("0x800700AA", StringComparison.Ordinal)) _mtpProbe.BackOffBusyInterface();
                                if (reason == _mtpPendingReason) return;
                                _mtpPendingReason = reason;
                                SessionLog.Write("WPD", reason);
                            });
                            _cancellation.Token.ThrowIfCancellationRequested();
                            if (found is not null && ReferenceEquals(announcement, _mtpAnnouncement) && _mtpEnabled)
                            {
                                _wpd = found;
                                SessionLog.Write("WPD", $"USB 目录验证就绪，耗时 {Environment.TickCount64 - _mtpProbeStarted} ms，探测 {_mtpProbeAttempts} 次");
                                MtpAvailabilityChanged?.Invoke(true);
                            }
                        }
                        if (_wpd is not null) await SendMtp(new("ready") { Epoch = announcement?.Epoch });
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
                catch (Exception error) { SessionLog.Write("WPD", "USB 目录探测暂不可用", error); }
                try { await _mtpProbe.WaitAsync(_mtpEnabled, MtpReady, _cancellation.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }
    private void DropMtp()
    {
        _wpd = null;
        _mtpProbeStarted = 0; _mtpProbeAttempts = 0; _mtpPendingReason = null;
        MtpAvailabilityChanged?.Invoke(false);
        foreach (var id in _mtpJobs.Keys) _ = FailDroppedMtpAsync(id);
    }
    private async Task FailDroppedMtpAsync(Guid id)
    {
        const string reason = "USB 文件通道已断开，请重试传输";
        try
        {
            await HandleTransferFailureAsync(new(id, reason));
            await TrySendFailureAsync(id, reason);
        }
        catch (Exception error) { SessionLog.Write("WPD", "结束断开的 USB 文件任务失败", error); }
    }
    private Task SendMtp(MtpPacket packet) => Enqueue(WireMessageType.MtpControl, 0, JsonSerializer.SerializeToUtf8Bytes(packet, MtpJson));
    private Task HandleMtpAsync(byte[] payload)
    {
        if (!_negotiation.Supports(BtxCapability.MtpFiles) || payload.Length > 16 * 1024)
            throw new InvalidDataException("Invalid USB control message");
        var packet = JsonSerializer.Deserialize<MtpPacket>(payload, MtpJson) ?? throw new InvalidDataException("Empty USB control");
        if (packet.Op == "announce")
        {
            if (packet.Epoch?.Length != 32 || !Guid.TryParseExact(packet.Epoch, "N", out _) || packet.Path?.LastOrDefault() != "bluelink-usb-" + packet.Epoch)
                throw new InvalidDataException("Invalid USB epoch");
            if (_mtpAnnouncement?.Epoch != packet.Epoch)
            {
                DropMtp(); _mtpAnnouncement = packet;
                SessionLog.Write("WPD", "收到新的手机 USB 目录，立即检测");
                _mtpProbe.Request();
            }
            // Repeated announcements are heartbeats, not events: waking here would make hello/announce spin.
            return Task.CompletedTask;
        }
        if (packet.Op == "disabled") { _mtpAnnouncement = null; DropMtp(); return Task.CompletedTask; }
        if (packet.Op == "hello") { _mtpProbe.Request(); return Task.CompletedTask; }
        if (packet.Epoch != _mtpAnnouncement?.Epoch) return Task.CompletedTask;
        var id = Guid.Parse(packet.Id ?? throw new InvalidDataException("USB transfer id missing"));
        if (packet.Op == "queue")
        {
            var binding = _wpd;
            if (!MtpReady || binding is null) return TrySendFailureAsync(id, "USB 文件通道不可用，请重试");
            var offer = TransferWire.DecodeOffer(Convert.FromBase64String(packet.Offer ?? ""));
            if (offer.Id != id || _mtpJobs.Count >= 256 || _outgoing.ContainsKey(id) || _incoming.ContainsKey(id))
                throw new InvalidDataException("Invalid USB queued offer");
            var item = CreateIncomingItem(offer, TransferStatus.Queued);
            var job = new MtpJob(offer, new(item, _onTransfer), binding, packet.Epoch!, _cancellation.Token);
            if (!_mtpJobs.TryAdd(id, job)) throw new InvalidDataException("Duplicate USB queue entry");
            _onTransfer(item);
            _ = ServeMtpRequestAsync(job);
        }
        else if (_mtpJobs.TryGetValue(id, out var job))
        {
            switch (packet.Op)
            {
                case "blob":
                    if (job.Outgoing || !job.Active || job.ImportStarted) throw new InvalidDataException("Unexpected USB file");
                    job.ImportStarted = true;
                    job.ImportTask = ReceiveMtpBlobAsync(job, packet);
                    break;
                case "data": job.DataReady.TrySetResult(); break;
                case "end":
                    if (!_incoming.ContainsKey(id) && job.Progress.Item.Status == TransferStatus.Queued)
                        job.Progress.Report(TransferStatus.Canceled);
                    job.Done.TrySetResult(); break;
            }
        }
        return Task.CompletedTask;
    }
    private async Task ServeMtpRequestAsync(MtpJob job)
    {
        try
        {
            await job.Binding.Queue.RunAsync(async token =>
            {
                await job.Progress.WaitAsync(token);
                job.Active = true;
                SessionLog.Write("WPD", $"USB/WPD 接收队列开始，id={job.Offer.Id:N}，bytes={job.Offer.Size}");
                await SendMtp(new("start") { Id = job.Offer.Id.ToString(), Epoch = job.Epoch });
                try { await job.Done.Task.WaitAsync(token); }
                finally
                {
                    job.Stop.Cancel();
                    if (job.ImportTask is { } import) try { await import; } catch { }
                }
            }, job.Stop.Token);
        }
        catch (Exception error)
        {
            if (job.Progress.Item.Status is not (TransferStatus.Completed or TransferStatus.Canceled or TransferStatus.Failed))
            {
                job.Progress.Item.Status = error is OperationCanceledException ? TransferStatus.Canceled : TransferStatus.Failed;
                job.Progress.Item.FailureDetail = "USB 文件通道已结束，请重试";
                _onTransfer(job.Progress.Item);
                await TrySendFailureAsync(job.Offer.Id, job.Progress.Item.FailureDetail);
            }
        }
        finally { _mtpJobs.TryRemove(job.Offer.Id, out _); job.Stop.Cancel(); }
    }
    private async Task RunMtpOutgoingAsync(FileOffer offer, OutgoingTransfer state, Func<CancellationToken, Task> send, Action? onQueued)
    {
        while (_mtpJobs.Count >= 128) await Task.Delay(250, _cancellation.Token);
        var binding = _wpd ?? throw new IOException("USB 文件通道不可用");
        var job = new MtpJob(offer, state.Progress, binding, _mtpAnnouncement!.Epoch!, _cancellation.Token) { Outgoing = true };
        if (!_mtpJobs.TryAdd(offer.Id, job)) throw new IOException("Duplicate USB transfer");
        try
        {
            state.Progress.Report(TransferStatus.Queued, 0);
            SessionLog.Write("WPD", $"文件进入 USB/WPD 发送队列，id={offer.Id:N}，bytes={offer.Size}");
            await SendMtp(new("queue") { Id = offer.Id.ToString(), Epoch = job.Epoch, Offer = Convert.ToBase64String(TransferWire.EncodeOffer(offer)) });
            var queuedTransfer = binding.Queue.RunAsync(async token =>
            {
                await state.Progress.WaitAsync(token);
                job.Active = true;
                SessionLog.Write("WPD", $"USB/WPD 发送队列开始，id={offer.Id:N}");
                await SendMtp(new("start") { Id = offer.Id.ToString(), Epoch = job.Epoch });
                await send(token);
            }, job.Stop.Token);
            onQueued?.Invoke();
            await queuedTransfer;
        }
        finally
        {
            try { await SendMtp(new("end") { Id = offer.Id.ToString(), Epoch = job.Epoch }); } catch { }
            _mtpJobs.TryRemove(offer.Id, out _);
            job.Stop.Cancel();
        }
    }
    private async Task SendMtpBlobAsync(Guid id, Stream source, CancellationToken token)
    {
        var job = _mtpJobs[id];
        var key = RandomNumberGenerator.GetBytes(32);
        var name = Guid.NewGuid().ToString("N") + ".blm";
        Directory.CreateDirectory(OutgoingDirectory);
        var local = Path.Combine(OutgoingDirectory, name);
        try
        {
            await using (var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                await MtpFileCipher.EncryptAsync(source, output, id, job.Offer.Size, key, () => job.Progress.WaitAsync(token), token);
            long last = 0;
            await WpdFileHost.ExecuteAsync(job.Binding, device => device.Upload(job.Binding.FolderId, name, local,
                bytes => MtpProgress(job, bytes, ref last), () => job.Progress.WaitAsync(token).GetAwaiter().GetResult()),
                () => job.Progress.WaitAsync(token).GetAwaiter().GetResult(), token);
            await SendMtp(new("blob") { Id = id.ToString(), Epoch = job.Epoch, Blob = name, Key = Convert.ToBase64String(key) });
            await job.DataReady.Task.WaitAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            try { File.Delete(local); } catch { }
            // Android removes consumed blobs; cleanup on failure is best-effort, strictly inside this session directory.
            try { await WpdFileHost.ExecuteAsync(job.Binding, device => device.DeleteBlob(job.Binding.FolderId, name), () => { }, _cancellation.Token); } catch { }
        }
    }
    private async Task ReceiveMtpBlobAsync(MtpJob job, MtpPacket packet)
    {
        var id = job.Offer.Id;
        byte[]? key = null;
        string? local = null;
        try
        {
            if (!_incoming.TryGetValue(id, out var incoming)) throw new IOException("USB 文件尚未获得接收许可");
            key = Convert.FromBase64String(packet.Key ?? "");
            if (key.Length != 32) throw new InvalidDataException("Invalid USB file key");
            var blob = packet.Blob ?? "";
            WpdDevice.ValidateBlobName(blob);
            Directory.CreateDirectory(OutgoingDirectory);
            local = Path.Combine(OutgoingDirectory, Guid.NewGuid().ToString("N") + ".blm");
            var token = job.Stop.Token;
            long last = 0;
            await WpdFileHost.ExecuteAsync(job.Binding, device => device.Download(job.Binding.FolderId, blob, local,
                MtpFileCipher.EncodedSize(job.Offer.Size), bytes => MtpProgress(job, bytes, ref last),
                () => job.Progress.WaitAsync(token).GetAwaiter().GetResult()), () => job.Progress.WaitAsync(token).GetAwaiter().GetResult(), token);
            await using var input = File.OpenRead(local);
            long offset = 0;
            await MtpFileCipher.DecryptAsync(input, id, job.Offer.Size, key, async data =>
            {
                await incoming.Receiver.ImportChunkAsync(offset, data, token);
                offset += data.Length;
            }, () => job.Progress.WaitAsync(token), token);
            await SendMtp(new("data") { Id = id.ToString(), Epoch = job.Epoch });
        }
        catch (Exception error)
        {
            await HandleTransferFailureAsync(new(id, error is OperationCanceledException ? "USB 传输已取消" : "USB 文件读取或校验失败"));
            await TrySendFailureAsync(id, "USB 文件读取或校验失败，请重试");
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (local is not null) try { File.Delete(local); } catch { }
        }
    }
    private void MtpProgress(MtpJob job, long encodedBytes, ref long last)
    {
        var now = Environment.TickCount64;
        if (now - last < 250 && encodedBytes != MtpFileCipher.EncodedSize(job.Offer.Size)) return;
        last = now;
        var bytes = Math.Min(job.Offer.Size, (long)(encodedBytes / (double)MtpFileCipher.EncodedSize(job.Offer.Size) * job.Offer.Size));
        job.Progress.Report(TransferStatus.Transferring, bytes);
        SendMtp(new("progress") { Id = job.Offer.Id.ToString(), Epoch = job.Epoch, Bytes = bytes }).GetAwaiter().GetResult();
    }
    private void CancelMtp(Guid id, TransferStatus status = TransferStatus.Canceled)
    {
        if (_mtpJobs.TryGetValue(id, out var job))
        {
            if (!_incoming.ContainsKey(id) && job.Progress.Item.IsActive) job.Progress.Report(status);
            job.Stop.Cancel(); job.Done.TrySetResult();
        }
    }
    private sealed class MtpJob(FileOffer offer, TransferPauseController progress, WpdBinding binding, string epoch, CancellationToken token)
    {
        public FileOffer Offer { get; } = offer;
        public TransferPauseController Progress { get; } = progress;
        public WpdBinding Binding { get; } = binding;
        public string Epoch { get; } = epoch;
        public bool Outgoing, Active, ImportStarted;
        public Task? ImportTask;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DataReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record MtpPacket(string Op)
    {
        public string? Id { get; init; }
        public string? Epoch { get; init; }
        public string[]? Path { get; init; }
        public string? Proof { get; init; }
        public string? Offer { get; init; }
        public string? Blob { get; init; }
        public string? Key { get; init; }
        public long Bytes { get; init; }
    }
}
