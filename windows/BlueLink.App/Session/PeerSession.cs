using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BlueLink.Bluetooth;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Transfer;
using BlueLink.Transport;

namespace BlueLink.Session;

public sealed partial class PeerSession : IAsyncDisposable
{
    private const int FileExtentSize = 64 * 1024;
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExtentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);
    private readonly IPeerConnection _connection;
    private readonly bool _listenerRole;
    private readonly IdentityStore _identityStore;
    private readonly Action<TrustRequest> _presentTrust;
    private readonly string? _expectedTrustedPeerId;
    private readonly IdentityAssociationHandler? _identityAssociation;
    private readonly Action<ChatItem> _onMessage;
    private readonly Action<TransferItem> _onTransfer;
    private readonly Action<string> _onReady;
    private readonly Action<string> _onClosed;
    private readonly Action<ChatEnvelope, bool>? _onEnvelope;
    private readonly Action<ChatReceipt>? _onReceipt;
    private string _receiveDirectory;
    private long _maxReceiveBytes;
    private bool _autoAcceptFiles;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PriorityQueue<Outbound, (int Priority, long Order)> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);
    private readonly object _outboundLock = new();
    private readonly TransferStreamFence _transferStreams = new();
    private readonly ConcurrentDictionary<Guid, OutgoingTransfer> _outgoing = new();
    private readonly SemaphoreSlim _filePreparation = new(1, 1);
    internal TransferAttemptRegistry Attempts { get; set; } = new();
    private readonly ConcurrentDictionary<Guid, IncomingTransfer> _incoming = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _offerDecisions = new();
    private readonly object _receiveDecisionLock = new();
    public Func<IncomingFileDecision, CancellationToken, Task<string?>>? ReceiveDecision { get; set; }
    public string DuplicateFilePolicy { get; set; } = "rename";
    private readonly ConcurrentDictionary<Guid, PendingOffer> _pendingOffers = new();
    private long _order;
    private long _sendSequence;
    private SessionKeys? _keys;
    private BtxNegotiation _negotiation = new(1, 0, BtxCapability.None);
    private Task? _writer;
    private int _disposed;
    private int _closing;
    private UsbSessionLiveness? _usbLiveness;
    private Task? _heartbeat;

    public PeerSession(IPeerConnection connection, bool listenerRole, IdentityStore identityStore,
        Action<TrustRequest> presentTrust, Action<ChatItem> onMessage,
        Action<TransferItem> onTransfer, Action<string> onReady, Action<string> onClosed,
        Action<ChatEnvelope, bool>? onEnvelope = null, Action<ChatReceipt>? onReceipt = null,
        string? receiveDirectory = null, long maxReceiveBytes = long.MaxValue,
        bool autoAcceptFiles = true, string? expectedTrustedPeerId = null, IdentityAssociationHandler? identityAssociation = null)
    {
        _connection = connection;
        _listenerRole = listenerRole;
        _identityStore = identityStore;
        _presentTrust = presentTrust;
        _expectedTrustedPeerId = expectedTrustedPeerId;
        _identityAssociation = identityAssociation;
        _onMessage = onMessage;
        _onTransfer = item => onTransfer(_transferStreams.Stamp(item));
        _onReady = onReady;
        _onClosed = onClosed;
        _onEnvelope = onEnvelope;
        _onReceipt = onReceipt;
        _receiveDirectory = string.IsNullOrWhiteSpace(receiveDirectory)
            ? Path.Combine(BlueLink.Storage.AppStoragePaths.UserDirectory, "Received")
            : receiveDirectory;
        _maxReceiveBytes = maxReceiveBytes;
        _autoAcceptFiles = autoAcceptFiles;
    }

    public string LocalDeviceName { get; set; } = "";
    private string _remoteDeviceName = "";
    public bool HasPeerProvidedName => !string.IsNullOrWhiteSpace(_remoteDeviceName);
    public string PeerName => string.IsNullOrWhiteSpace(_remoteDeviceName) ? _connection.PeerName : _remoteDeviceName;

    public string ReceiveDirectory { get => _receiveDirectory; set => _receiveDirectory = value; }
    public string OutgoingDirectory { get; set; } = Path.Combine(BlueLink.Storage.AppStoragePaths.UserDirectory, "Cache", "Outgoing");
    public long MaxReceiveBytes
    {
        get => Interlocked.Read(ref _maxReceiveBytes);
        set
        {
            Interlocked.Exchange(ref _maxReceiveBytes, value);
            _ = ReconsiderPendingOffersAsync();
        }
    }
    public bool AutoAcceptFiles
    {
        get => _autoAcceptFiles;
        set
        {
            _autoAcceptFiles = value;
            if (value) _ = ReconsiderPendingOffersAsync();
        }
    }

    public async Task RunAsync()
    {
        var stage = "安全握手";
        try
        {
            SessionLog.Write("Session", $"会话开始，peer={_connection.PeerName}，role={(_listenerRole ? "listener" : "dialer")}");
            var handshake = await SecureConnectionHandshake.RunAsync(_connection.Input, _connection.Output, _listenerRole,
                _identityStore, _connection.PeerName, _presentTrust, _cancellation.Token, _expectedTrustedPeerId, association: _identityAssociation, localDeviceName: LocalDeviceName);
            _remoteDeviceName = handshake.RemoteDeviceName;
            _keys = handshake.Keys;
            _cancellation.Token.ThrowIfCancellationRequested();
            _negotiation = handshake.Negotiation;
            var replay = handshake.Replay;
            _sendSequence = 1; // PROTOCOL_HELLO was encrypted as sequence zero during confirmation.
            _writer = WriterLoopAsync(_cancellation.Token);
            _onReady(Convert.ToHexString(_keys.RemotePeerId));
            StartMtp();
            SessionLog.Write("Session", $"安全会话已建立，peer={_connection.PeerName}，BTX={_negotiation.Major}.{_negotiation.Minor}，caps=0x{(uint)_negotiation.Capabilities:X}");
            stage = "已连接会话";
            if (_connection.Transport == TransportKind.Usb)
            {
                _usbLiveness = new();
                _heartbeat = _usbLiveness.RunAsync(() => Enqueue(WireMessageType.Ping, 0, []), reason =>
                {
                    _onClosed(reason);
                    _cancellation.Cancel();
                }, _cancellation.Token);
            }
            await ReadLoopAsync(replay, _cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (TrustHandshakeException failure)
        {
            _onClosed(Localization.Strings.Get(failure.Message));
        }
        catch (EndOfStreamException failure)
        {
            SessionLog.Write("Session", $"对端在{stage}阶段关闭连接", failure);
            _onClosed($"对端在{stage}阶段关闭了连接；请打开 Android 的“诊断”页查看具体原因");
        }
        catch (Exception failure)
        {
            SessionLog.Write("Session", $"{stage}失败", failure);
            _onClosed($"{stage}失败：{failure.Message}");
        }
        finally
        {
            await DisposeAsync();
            if (_keys is { } keys)
            {
                CryptographicOperations.ZeroMemory(keys.SendKey);
                CryptographicOperations.ZeroMemory(keys.ReceiveKey);
            }
        }
    }

    public async Task SendChatAsync(string text, Guid? messageId = null)
    {
        if (_keys is null || string.IsNullOrWhiteSpace(text)) return;
        var content = text.Trim();
        var payload = _negotiation.Supports(BtxCapability.StructuredMessages)
            ? MessageWire.Encode(new(messageId ?? Guid.NewGuid(), ChatPayloadKind.Text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), content, []))
            : Encoding.UTF8.GetBytes(content);
        await Enqueue(WireMessageType.Chat, 1, payload);
    }

    public async Task SendFileAsync(string path, string? displayName = null, Action? onAnnounced = null)
    {
        using var preparation = await FilePreparationLease.EnterAsync(_filePreparation, _cancellation.Token);
        var name = displayName ?? Path.GetFileName(path);
        var mimeType = MimeTypeFor(name);
        var originalId = Guid.NewGuid();
        using var attempt = Attempts.Begin(originalId);
        var snapshotId = Guid.NewGuid();
        var snapshotRoot = OutgoingDirectory;
        var snapshotPath = await OutgoingSnapshot.CreateAsync(path, snapshotRoot, snapshotId, _cancellation.Token, originalId);
        SessionLog.Write("Transfer", $"已创建不可变发送快照，source={name}，snapshot={Path.GetFileName(snapshotPath)}");
        try
        {
        var structured = _negotiation.Supports(BtxCapability.StructuredMessages | BtxCapability.AttachmentMetadata);
        if (!structured)
        {
            var legacyId = originalId;
            var legacy = await DescribeAsync(snapshotPath, legacyId, name, mimeType, AttachmentRole.File, null, null);
            await SendPreparedTransferAsync(snapshotPath, legacy, path, attempt: attempt);
            return;
        }

        var messageId = Guid.NewGuid();
        var originalAttachmentId = Guid.NewGuid();
        var originalRole = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? AttachmentRole.ImageOriginal : AttachmentRole.File;
        var original = await DescribeAsync(snapshotPath, originalId, name, mimeType, originalRole,
            messageId, originalAttachmentId);
        var originalTransfer = new TransferItem
        {
            Id = original.Id,
            Name = original.Name,
            TotalBytes = original.Size,
            Outgoing = true,
            Status = TransferStatus.Queued,
            MimeType = original.MimeType,
            LocalPath = path,
            MessageId = messageId,
            AttachmentId = originalAttachmentId,
            Role = originalRole,
            SourceSha256 = Convert.ToHexString(original.Hash)
        };
        attempt.Publish(originalTransfer, _onTransfer);
        ImagePreviewBuilder.PreviewAsset? previewAsset = null;
        PreparedTransfer? preview = null;
        try
        {
            if (originalRole == AttachmentRole.ImageOriginal)
            {
                try
                {
                    var previewId = Guid.NewGuid();
                    previewAsset = await ImagePreviewBuilder.CreateAsync(snapshotPath, previewId, _cancellation.Token);
                    var previewName = $"{Path.GetFileNameWithoutExtension(name)}.preview{Path.GetExtension(previewAsset.FileName)}";
                    preview = await DescribeAsync(previewAsset.Path, previewId, previewName,
                        previewAsset.MimeType, AttachmentRole.ImagePreview, messageId, Guid.NewGuid());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception failure)
                {
                    SessionLog.Write("Transfer", "无法生成图片预览，将继续发送原图", failure);
                }
            }

            var descriptors = new List<AttachmentDescriptor>();
            if (preview is not null) descriptors.Add(preview.Descriptor);
            descriptors.Add(original.Descriptor);
            var envelope = new ChatEnvelope(messageId,
                originalRole == AttachmentRole.File ? ChatPayloadKind.File : ChatPayloadKind.Image,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "", descriptors);
            await Enqueue(WireMessageType.Chat, 1, MessageWire.Encode(envelope));
            _onEnvelope?.Invoke(envelope, true);
            onAnnounced?.Invoke();
            if (preview is not null && previewAsset is not null)
            {
                try { await SendPreparedTransferAsync(previewAsset.Path, preview, previewAsset.Path); }
                catch (OperationCanceledException) { throw; }
                catch (Exception failure)
                {
                    SessionLog.Write("Transfer", $"图片预览发送失败，继续发送原图，id={preview.Id:N}", failure);
                }
            }
            await SendPreparedTransferAsync(snapshotPath, original, path, originalTransfer, preparation.Dispose, attempt);
        }
        catch (Exception failure)
        {
            if (originalTransfer.Status == TransferStatus.Queued)
            {
                originalTransfer.Status = failure is OperationCanceledException
                    ? TransferStatus.Canceled : TransferStatus.Failed;
                originalTransfer.FailureDetail = failure.Message;
                attempt.Publish(originalTransfer, _onTransfer);
            }
            throw;
        }
        finally
        {
            if (previewAsset is not null)
                try { File.Delete(previewAsset.Path); } catch { }
        }
        }
        finally
        {
            try { File.Delete(snapshotPath); }
            catch (Exception failure) { SessionLog.Write("Transfer", "清理发送快照失败，将由缓存清理器处理", failure); }
            finally { Storage.OwnedTemporaryFiles.Release(snapshotPath); }
        }
    }

    public async Task RetryFileAsync(string path, TransferItem template, bool forceBluetooth = false)
    {
        if (!template.Outgoing || template.Id == Guid.Empty)
            throw new InvalidOperationException("仅可重试有效的发送任务");
        using var attempt = Attempts.Begin(template.Id);
        var snapshotRoot = OutgoingDirectory;
        var snapshotPath = await OutgoingSnapshot.CreateAsync(path, snapshotRoot, Guid.NewGuid(), _cancellation.Token, template.Id);
        try
        {
            var prepared = await DescribeAsync(snapshotPath, template.Id, template.Name, template.MimeType,
                template.Role, template.MessageId, template.AttachmentId);
            TransferSourceIdentity.Validate(template, prepared.Size, prepared.Hash);
            var retry = SessionTransferLedger.Copy(template);
            retry.FailureDetail = null;
            await SendPreparedTransferAsync(snapshotPath, prepared, path, retry, attempt: attempt, forceBluetooth: forceBluetooth);
        }
        finally
        {
            try { File.Delete(snapshotPath); }
            catch (Exception failure) { SessionLog.Write("Transfer", "清理重试快照失败，将由缓存清理器处理", failure); }
            finally { Storage.OwnedTemporaryFiles.Release(snapshotPath); }
        }
    }

    private async Task<PreparedTransfer> DescribeAsync(string path, Guid id, string name, string mimeType,
        AttachmentRole role, Guid? messageId, Guid? attachmentId)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileExtentSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = input.Length;
        var hash = await SHA256.HashDataAsync(input, _cancellation.Token);
        if (input.Length != size) throw new IOException($"文件在哈希期间发生变化：expected={size}, actual={input.Length}");
        var descriptor = new AttachmentDescriptor(attachmentId ?? Guid.Empty, id, role, name, mimeType, size, hash);
        return new(id, name, mimeType, size, hash, role, messageId, attachmentId, descriptor);
    }

    private async Task SendPreparedTransferAsync(string path, PreparedTransfer prepared, string localPath,
        TransferItem? existingTransfer = null, Action? onQueued = null, TransferAttemptRegistry.Lease? attempt = null, bool forceBluetooth = false)
    {
        var id = prepared.Id;
        using var transferStream = _transferStreams.StartOutgoing(id, _negotiation.Supports(BtxCapability.TransferAttemptStreams), _listenerRole);
        using var ownedAttempt = attempt is null ? Attempts.Begin(id) : null;
        attempt ??= ownedAttempt!;
        void Publish(TransferItem value) => attempt.Publish(value, _onTransfer);
        var transfer = existingTransfer ?? new TransferItem
        {
            Id = id, Name = prepared.Name, TotalBytes = prepared.Size, Outgoing = true,
            Status = TransferStatus.Offered, MimeType = prepared.MimeType, LocalPath = localPath,
            MessageId = prepared.MessageId, AttachmentId = prepared.AttachmentId, Role = prepared.Role
        };
        transfer.SourceSha256 = Convert.ToHexString(prepared.Hash);
        transfer.Status = TransferStatus.Queued;
        Publish(transfer);
        var state = new OutgoingTransfer(transfer, Publish);
        _outgoing[id] = state;
        var offer = new FileOffer(id, prepared.Name, prepared.Size, FileExtentSize, prepared.Hash,
            prepared.MessageId, prepared.AttachmentId, prepared.MimeType, prepared.Role);
        var useMtp = MtpReady && !forceBluetooth;
        try
        {
            if (useMtp) await RunMtpOutgoingAsync(offer, state, SendCoreAsync, onQueued);
            else await SendCoreAsync(_cancellation.Token);
        }
        catch (Exception failure)
        {
            transfer.Status = failure is OperationCanceledException && transfer.Status != TransferStatus.Failed ? TransferStatus.Canceled : TransferStatus.Failed;
            transfer.FailureDetail = failure.Message;
            Publish(transfer);
            SessionLog.Write("Transfer", $"文件发送失败，id={id:N}，name={prepared.Name}", failure);
            if (failure is not (RemoteTransferException or OperationCanceledException)) await TrySendFailureAsync(id, failure.Message);
            throw;
        }
        finally
        {
            _outgoing.TryRemove(id, out _);
            state.Fail(new OperationCanceledException("文件传输已结束"));
        }
        async Task SendCoreAsync(CancellationToken token)
        {
            state.Progress.Report(TransferStatus.Offered);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileExtentSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != prepared.Size) throw new IOException("待发送文件在准备后发生变化");
            SessionLog.Write("Transfer", $"发送 Offer，id={id:N}，name={prepared.Name}，bytes={prepared.Size}，role={prepared.Role}");
            await Enqueue(WireMessageType.TransferOffer, 2, TransferWire.EncodeOffer(offer));
            var startIndex = await state.Accepted.Task.WaitAsync(OfferTimeout, token);
            var startOffset = Math.Min(prepared.Size, (long)startIndex * FileExtentSize);
            if (!useMtp)
            {
                input.Position = startOffset;
                state.Progress.Report(startOffset > 0 ? TransferStatus.Resuming : TransferStatus.Transferring, startOffset);
                SessionLog.Write("Transfer", $"从持久化断点继续，id={id:N}，extent={startIndex}，bytes={startOffset}");
            }

            if (useMtp) await SendMtpBlobAsync(id, input, token);
            else
            {
            var buffer = new byte[FileExtentSize];
            var index = startIndex;
            while (true)
            {
                await state.Progress.WaitAsync(token);
                var read = await ReadAtMostAsync(input, buffer, token);
                if (read == 0) break;
                var data = buffer[..read];
                var acknowledgement = state.ExpectExtent(index);
                await Enqueue(WireMessageType.TransferExtent, 2,
                    TransferWire.EncodeExtent(new(id, index, SHA256.HashData(data), data)));
                await acknowledgement.Task.WaitAsync(ExtentTimeout, token);
                state.RemoveExtent(index);
                index++;
                state.Progress.Report(TransferStatus.Transferring, transfer.CompletedBytes + read);
            }
            }
            await state.Progress.WaitAsync(token);
            await Enqueue(WireMessageType.TransferFinish, 2, TransferWire.EncodeId(id));
            state.Progress.Report(TransferStatus.Verifying);
            await state.Completed.Task.WaitAsync(CompletionTimeout, token);
            state.Progress.Report(TransferStatus.Completed, transfer.TotalBytes);
            SessionLog.Write("Transfer", $"对端已校验并落盘，id={id:N}，bytes={prepared.Size}");
        }
    }

    private sealed record PreparedTransfer(Guid Id, string Name, string MimeType, long Size, byte[] Hash,
        AttachmentRole Role, Guid? MessageId, Guid? AttachmentId, AttachmentDescriptor Descriptor);

    public async Task CancelTransferAsync(Guid id, string reason = "用户取消")
    {
        using var captured = _transferStreams.Capture(id);
        CancelMtp(id);
        CancelOfferDecision(id);
        var cancellation = new OperationCanceledException(reason);
        if (_outgoing.TryGetValue(id, out var outgoing)) outgoing.Fail(cancellation);
        if (_pendingOffers.TryRemove(id, out var pending))
        {
            pending.Item.Status = TransferStatus.Canceled;
            pending.Item.FailureDetail = reason;
            _onTransfer(pending.Item);
        }
        if (_incoming.TryRemove(id, out var incoming))
        {

            await incoming.Receiver.DisposeAsync();
            incoming.Item.Status = TransferStatus.Canceled;
            _onTransfer(incoming.Item);
        }
        if (_negotiation.Supports(BtxCapability.TransferControl))
            await Enqueue(WireMessageType.TransferControl, 0, TransferControlWire.Encode(new(id,
                TransferControlAction.Cancel, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason)));
        else await TrySendFailureAsync(id, reason);
    }

    public Task PauseTransferAsync(Guid id) => ChangeLocalPauseAsync(id, true);
    public Task ResumeTransferAsync(Guid id) => ChangeLocalPauseAsync(id, false);
    private async Task ChangeLocalPauseAsync(Guid id, bool paused)
    {
        var progress = _outgoing.TryGetValue(id, out var outgoing) ? outgoing.Progress :
            _incoming.TryGetValue(id, out var incoming) ? incoming.Progress :
            _mtpJobs.TryGetValue(id, out var mtp) ? mtp.Progress : null;
        if (progress?.SetPaused(local: true, paused) != true) return;
        if (_negotiation.Supports(BtxCapability.TransferControl))
            await Enqueue(WireMessageType.TransferControl, 0, TransferControlWire.Encode(new(id,
                paused ? TransferControlAction.Pause : TransferControlAction.Resume,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), paused ? "用户暂停" : "用户继续")));
    }

    private async Task WriterLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(token);
            Outbound item;
            lock (_outboundLock)
            {
                if (!_outbound.TryDequeue(out item!, out _)) continue;
            }
            try
            {
                var keys = _keys ?? throw new InvalidOperationException("Session keys unavailable");
                EnsureTrusted(keys);
                await BtxRecordCodec.WriteAsync(_connection.Output,
                    new(item.Type, 0, item.Stream, _sendSequence++, item.Payload), keys.SendKey, keys.SendNoncePrefix, token);
                item.Completion.TrySetResult();
            }
            catch (Exception failure)
            {
                item.Completion.TrySetException(failure);
                _cancellation.Cancel();
                throw;
            }
        }
    }

    private Task Enqueue(WireMessageType type, int stream, byte[] payload)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (TransferRouting(type, payload) is { } route)
            stream = _transferStreams.StreamFor(route.Id, stream, _negotiation.Supports(BtxCapability.TransferAttemptStreams));
        var value = new Outbound(type, stream, payload, completion);
        lock (_outboundLock)
        {
            if (_cancellation.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 ||
                (Volatile.Read(ref _closing) != 0 && type != WireMessageType.GoAway))
                return Task.FromCanceled(new CancellationToken(true));
            if (_keys is { } keys) EnsureTrusted(keys);
            _outbound.Enqueue(value, (WireMessagePriority.Of(type), _order++));
            _outboundSignal.Release();
        }
        return completion.Task;
    }

    private static (Guid Id, bool Starts)? TransferRouting(WireMessageType type, byte[] payload)
    {
        if (type == WireMessageType.TransferOffer) return (TransferWire.DecodeOffer(payload).Id, true);
        if (type == WireMessageType.TransferControl) return (TransferControlWire.Decode(payload).TransferId, false);
        if (type == WireMessageType.MtpControl)
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("id", out var id) || id.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
            return (Guid.Parse(id.GetString()!), json.RootElement.GetProperty("op").GetString() == "queue");
        }
        return type is WireMessageType.TransferAccept or WireMessageType.TransferExtent or WireMessageType.TransferFinish
            or WireMessageType.TransferExtentAck or WireMessageType.TransferComplete or WireMessageType.TransferReject or WireMessageType.TransferFailed
            ? (TransferWire.DecodeId(payload), false) : null;
    }

    private async Task ReadLoopAsync(ReplayGuard replay, CancellationToken token)
    {
        await foreach (var frame in ReadFramesAsync(replay, token))
        {
            IDisposable? attemptScope = null;
            if (TransferRouting(frame.Type, frame.Payload) is { } route)
            {
                attemptScope = _transferStreams.Receive(route.Id, frame.StreamId, route.Starts,
                    _incoming.ContainsKey(route.Id) || _outgoing.ContainsKey(route.Id) || _pendingOffers.ContainsKey(route.Id)
                        || _offerDecisions.ContainsKey(route.Id) || _mtpJobs.ContainsKey(route.Id),
                    _negotiation.Supports(BtxCapability.TransferAttemptStreams), _listenerRole);
                if (attemptScope is null) continue;
            }
            using (attemptScope)
            switch (frame.Type)
            {
                case WireMessageType.Chat:
                    await ReceiveChatAsync(frame.Payload);
                    break;
                case WireMessageType.ChatReceipt:
                    if (_negotiation.Supports(BtxCapability.MessageReceipts))
                        _onReceipt?.Invoke(MessageWire.DecodeReceipt(frame.Payload));
                    break;
                case WireMessageType.Ping: _ = Enqueue(WireMessageType.Pong, 0, frame.Payload); break;
                case WireMessageType.GoAway: _onClosed("对端已结束连接。"); return;
                case WireMessageType.TransferOffer: await ReceiveOfferAsync(TransferWire.DecodeOffer(frame.Payload)); break;
                case WireMessageType.TransferAccept:
                    var accept = TransferWire.DecodeAccept(frame.Payload);
                    if (_outgoing.TryGetValue(accept.Id, out var accepted)) accepted.Accepted.TrySetResult(accept.NextExtent);
                    break;
                case WireMessageType.TransferExtent: await ReceiveExtentAsync(TransferWire.DecodeExtent(frame.Payload), token); break;
                case WireMessageType.TransferFinish: await FinishTransferAsync(TransferWire.DecodeId(frame.Payload), token); break;
                case WireMessageType.TransferExtentAck:
                    var acknowledgement = TransferWire.DecodeExtentAck(frame.Payload);
                    if (_outgoing.TryGetValue(acknowledgement.Id, out var extentState))
                        extentState.AcknowledgeExtent(acknowledgement.Index);
                    break;
                case WireMessageType.TransferComplete:
                    if (_outgoing.TryGetValue(TransferWire.DecodeId(frame.Payload), out var completed))
                        completed.Completed.TrySetResult();
                    break;
                case WireMessageType.TransferReject:
                    await HandleTransferFailureAsync(new(TransferWire.DecodeId(frame.Payload), "对端拒绝了文件传输"));
                    break;
                case WireMessageType.TransferFailed:
                    await HandleTransferFailureAsync(TransferWire.DecodeFailure(frame.Payload));
                    break;
                case WireMessageType.TransferControl:
                    await HandleTransferControlAsync(TransferControlWire.Decode(frame.Payload));
                    break;
                case WireMessageType.MtpControl: await HandleMtpAsync(frame.Payload); break;
            }
        }
    }

    private async IAsyncEnumerable<BtxFrame> ReadFramesAsync(ReplayGuard replay,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (_connection.Transport != TransportKind.Usb)
        {
            while (!token.IsCancellationRequested)
            {
                var keys = _keys!;
                var frame = await BtxRecordCodec.ReadAsync(_connection.Input, keys.ReceiveKey, keys.ReceiveNoncePrefix, replay, token);
                EnsureTrusted(keys);
                yield return frame;
            }
            yield break;
        }
        // A slow file hash/commit must not prevent authenticated heartbeat replies.
        // Keep a single wire reader and bounded application backpressure.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var frames = System.Threading.Channels.Channel.CreateBounded<BtxFrame>(8);
        var pump = PumpUsbRecordsAsync(frames.Writer, replay, stop.Token);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token)) yield return frame;
        }
        finally { stop.Cancel(); await pump; }
    }

    private async Task PumpUsbRecordsAsync(System.Threading.Channels.ChannelWriter<BtxFrame> frames,
        ReplayGuard replay, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var keys = _keys!;
                var frame = await BtxRecordCodec.ReadAsync(_connection.Input, keys.ReceiveKey, keys.ReceiveNoncePrefix, replay, token);
                EnsureTrusted(keys);
                _usbLiveness?.Received();
                if (frame.Type == WireMessageType.Ping) { _ = Enqueue(WireMessageType.Pong, 0, frame.Payload); continue; }
                if (frame.Type == WireMessageType.Pong) continue;
                if (frame.Type == WireMessageType.GoAway)
                {
                    _onClosed("对端已结束连接。");
                    _cancellation.Cancel();
                    return;
                }
                await frames.WriteAsync(frame, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure) { error = failure; }
        finally { frames.TryComplete(error); }
    }

    private async Task ReceiveChatAsync(byte[] payload)
    {
        if (_negotiation.Supports(BtxCapability.StructuredMessages) && MessageWire.IsStructured(payload))
        {
            var envelope = MessageWire.Decode(payload);
            _onEnvelope?.Invoke(envelope, false);
            if (envelope.Kind is ChatPayloadKind.Text or ChatPayloadKind.System || !string.IsNullOrWhiteSpace(envelope.Body))
                _onMessage(new(envelope.MessageId, envelope.Body, false,
                    DateTimeOffset.FromUnixTimeMilliseconds(envelope.CreatedAt), MessageStatus.Received));
            if (_negotiation.Supports(BtxCapability.MessageReceipts))
                await Enqueue(WireMessageType.ChatReceipt, 1, MessageWire.EncodeReceipt(new(envelope.MessageId,
                    ReceiptState.Delivered, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            return;
        }
        _onMessage(new(Guid.NewGuid(), Encoding.UTF8.GetString(payload), false, DateTimeOffset.Now, MessageStatus.Received));
    }

    private async Task HandleTransferControlAsync(TransferControl control)
    {
        if (!_negotiation.Supports(BtxCapability.TransferControl))
            throw new InvalidDataException("对端发送了未协商的传输控制消息");
        if (control.Action is TransferControlAction.Pause or TransferControlAction.Resume)
        {
            var progress = _outgoing.TryGetValue(control.TransferId, out var outgoingProgress) ? outgoingProgress.Progress :
                _incoming.TryGetValue(control.TransferId, out var incomingProgress) ? incomingProgress.Progress :
                _mtpJobs.TryGetValue(control.TransferId, out var mtp) ? mtp.Progress : null;
            progress?.SetPaused(local: false, control.Action == TransferControlAction.Pause);
            SessionLog.Write("Transfer", $"对端传输控制，id={control.TransferId:N}，action={control.Action}");
            return;
        }
        if (control.Action != TransferControlAction.Cancel)
        {
            SessionLog.Write("Transfer", $"已收到暂不支持的控制指令，id={control.TransferId:N}，action={control.Action}");
            return;
        }
        CancelOfferDecision(control.TransferId);
        CancelMtp(control.TransferId);
        if (_pendingOffers.TryRemove(control.TransferId, out var pendingOffer))
        {
            pendingOffer.Item.Status = TransferStatus.Canceled;
            _onTransfer(pendingOffer.Item);
        }
        var failure = new OperationCanceledException(string.IsNullOrWhiteSpace(control.Reason) ? "对端取消传输" : control.Reason);
        if (_outgoing.TryGetValue(control.TransferId, out var outgoing)) outgoing.Fail(failure);
        if (_incoming.TryRemove(control.TransferId, out var incoming))
        {

            await incoming.Receiver.DisposeAsync();
            incoming.Item.Status = TransferStatus.Canceled;
            _onTransfer(incoming.Item);
        }
        SessionLog.Write("Transfer", $"对端取消文件传输，id={control.TransferId:N}");
    }

    private async Task ReceiveOfferAsync(FileOffer offer)
    {
        try
        {
            if (_mtpJobs.TryGetValue(offer.Id, out var queued) && (!queued.Active || queued.Outgoing ||
                !TransferWire.EncodeOffer(offer).SequenceEqual(TransferWire.EncodeOffer(queued.Offer))))
                throw new InvalidDataException("USB Offer 不属于当前排队任务");
            if (_incoming.ContainsKey(offer.Id) || _pendingOffers.ContainsKey(offer.Id) || _offerDecisions.ContainsKey(offer.Id))
                throw new InvalidDataException("重复文件 Offer");
            if (offer.Size > _maxReceiveBytes)
            {
                var pendingItem = CreateIncomingItem(offer, TransferStatus.Offered);
                pendingItem.FailureDetail = $"超过当前接收限制（{_maxReceiveBytes} B）；30 秒内提高限制可继续接收";
                _pendingOffers[offer.Id] = new(offer, pendingItem, DateTimeOffset.UtcNow + OfferTimeout);
                _onTransfer(pendingItem);
                SessionLog.Write("Transfer", $"暂缓超限 Offer，id={offer.Id:N}，bytes={offer.Size}，limit={_maxReceiveBytes}");
                _ = ExpirePendingOfferAsync(offer.Id);
                return;
            }
            _ = DecideOfferAsync(offer);
        }
        catch (Exception failure)
        {
            _onTransfer(new TransferItem { Id = offer.Id, Name = Path.GetFileName(offer.Name), TotalBytes = offer.Size,
                Outgoing = false, Status = TransferStatus.Failed, MessageId = offer.MessageId,
                AttachmentId = offer.AttachmentId, MimeType = offer.MimeType, Role = offer.Role,
                FailureDetail = failure.Message });
            SessionLog.Write("Transfer", $"拒绝 Offer，id={offer.Id:N}，name={offer.Name}", failure);
            await TrySendFailureAsync(offer.Id, failure.Message);
        }
    }

    private TransferItem CreateIncomingItem(FileOffer offer, TransferStatus status) => new()
    {
        Id = offer.Id,
        Name = Path.GetFileName(offer.Name),
        TotalBytes = offer.Size,
        Outgoing = false,
        Status = status,
        MessageId = offer.MessageId,
        AttachmentId = offer.AttachmentId,
        MimeType = offer.MimeType,
        Role = offer.Role,
    };

    private void CancelOfferDecision(Guid id)
    {
        lock (_receiveDecisionLock)
            if (_offerDecisions.TryGetValue(id, out var decision)) decision.Cancel();
    }

    private async Task DecideOfferAsync(FileOffer offer, TransferItem? existing = null, DateTimeOffset? expiresAt = null)
    {
        using var decision = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        var remaining = expiresAt is { } expiry ? expiry - DateTimeOffset.UtcNow : OfferTimeout;
        decision.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        if (!_offerDecisions.TryAdd(offer.Id, decision)) return;
        var item = existing ?? CreateIncomingItem(offer, TransferStatus.Offered);
        try
        {
            var policy = BlueLink.Transfer.DuplicateFilePolicy.Normalize(DuplicateFilePolicy);
            var requiresConfirmation = !_autoAcceptFiles && offer.Role != AttachmentRole.ImagePreview;
            var target = Path.Combine(_receiveDirectory, Path.GetFileName(offer.Name));
            var conflict = offer.Role != AttachmentRole.ImagePreview && (File.Exists(target) || Directory.Exists(target));
            if (requiresConfirmation || (policy == "ask" && conflict))
            {
                item.Status = TransferStatus.Offered;
                item.FailureDetail = "等待接收确认（30 秒）";
                _onTransfer(item);
                var handler = ReceiveDecision;
                policy = handler is null ? null : await handler(new(_connection.PeerName, offer,
                    requiresConfirmation, conflict, policy), decision.Token).WaitAsync(decision.Token);
                if (policy is null) throw new OperationCanceledException("用户拒绝接收");
            }
            await AcceptOfferAsync(offer, item, policy!, decision.Token);
        }
        catch (Exception failure)
        {
            item.Status = failure is OperationCanceledException ? TransferStatus.Canceled : TransferStatus.Failed;
            item.FailureDetail = failure is OperationCanceledException ? "接收已取消或确认超时" : failure.Message;
            _onTransfer(item);
            await TrySendFailureAsync(offer.Id, item.FailureDetail);
        }
        finally { lock (_receiveDecisionLock) _offerDecisions.TryRemove(offer.Id, out _); }
    }

    private async Task AcceptOfferAsync(FileOffer offer, TransferItem? existing, string policy, CancellationToken token)
    {
        var safeName = Path.GetFileName(offer.Name);
        var normalized = offer with { Name = safeName };
        var root = offer.Role == AttachmentRole.ImagePreview
            ? Path.Combine(BlueLink.Storage.AppStoragePaths.UserDirectory, "Cache", "Previews")
            : _receiveDirectory;
        var transfer = existing ?? (_mtpJobs.TryGetValue(offer.Id, out var queuedJob) ? queuedJob.Progress.Item : CreateIncomingItem(offer, TransferStatus.Offered));
        transfer.FailureDetail = null;
        transfer.Status = TransferStatus.Offered;
        lock (_receiveDecisionLock)
        {
            token.ThrowIfCancellationRequested();
            if (offer.Size > MaxReceiveBytes) throw new InvalidDataException("文件超过更新后的接收上限，请重新发送");
            // An unconfirmed collision appearing after the prompt must never silently overwrite a file.
            _incoming[offer.Id] = new(normalized, transfer, new TransferReceiver(root, normalized,
                offer.Role == AttachmentRole.ImagePreview ? "rename" : policy), _onTransfer);
            if (_mtpJobs.TryGetValue(offer.Id, out var job)) _incoming[offer.Id].Progress = job.Progress;
        }
        _onTransfer(transfer);
        SessionLog.Write("Transfer", $"接受 Offer，id={offer.Id:N}，name={safeName}，bytes={offer.Size}，block={offer.ExtentSize}");
        var committedBytes = _incoming[offer.Id].Receiver.ContiguousBytes;
        var nextExtent = checked((int)(committedBytes / offer.ExtentSize));
        await Enqueue(WireMessageType.TransferAccept, 2, _negotiation.Supports(BtxCapability.ResumeState)
            ? TransferWire.EncodeAccept(new(offer.Id, nextExtent)) : TransferWire.EncodeId(offer.Id));
    }

    private async Task ReconsiderPendingOffersAsync()
    {
        var decisions = new List<Task>();
        foreach (var pair in _pendingOffers.ToArray())
        {
            if (pair.Value.Offer.Size > _maxReceiveBytes || pair.Value.ExpiresAt <= DateTimeOffset.UtcNow) continue;
            if (!_pendingOffers.TryRemove(pair.Key, out var pending)) continue;
            // Raising the limit neither extends the sender's deadline nor lets
            // one confirmation prevent other pending offers being reconsidered.
            decisions.Add(DecideOfferAsync(pending.Offer, pending.Item, pending.ExpiresAt));
        }
        await Task.WhenAll(decisions);
    }

    private async Task ExpirePendingOfferAsync(Guid id)
    {
        try { await Task.Delay(OfferTimeout, _cancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (!_pendingOffers.TryRemove(id, out var pending)) return;
        pending.Item.Status = TransferStatus.Failed;
        pending.Item.FailureDetail = "文件 Offer 已过期，请对端重新发送";
        _onTransfer(pending.Item);
        await TrySendFailureAsync(id, pending.Item.FailureDetail);
    }

    private async Task ReceiveExtentAsync(FileExtent extent, CancellationToken token)
    {
        if (!_incoming.TryGetValue(extent.Id, out var incoming))
        {
            await TrySendFailureAsync(extent.Id, "未知文件传输");
            return;
        }
        try
        {
            await incoming.Receiver.AcceptAsync(extent, token);
            incoming.Progress.Report(TransferStatus.Transferring, incoming.Receiver.ContiguousBytes);
            await Enqueue(WireMessageType.TransferExtentAck, 2,
                TransferWire.EncodeExtentAck(new(extent.Id, extent.Index)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            _incoming.TryRemove(extent.Id, out _);

            try { await incoming.Receiver.DisposeAsync(); } catch (Exception disposeFailure)
            {
                SessionLog.Write("Transfer", $"关闭失败的接收文件失败，id={extent.Id:N}", disposeFailure);
            }
            incoming.Item.Status = TransferStatus.Failed;
            _onTransfer(incoming.Item);
            SessionLog.Write("Transfer", $"接收数据块失败，id={extent.Id:N}，index={extent.Index}，bytes={extent.Data.Length}", failure);
            await TrySendFailureAsync(extent.Id, failure.Message);
        }
    }

    private async Task FinishTransferAsync(Guid id, CancellationToken token)
    {
        if (!_incoming.TryGetValue(id, out var incoming))
        {
            await TrySendFailureAsync(id, "未知文件传输");
            return;
        }
        incoming.Item.Status = TransferStatus.Verifying;
        _onTransfer(incoming.Item);
        SessionLog.Write("Transfer", $"数据块接收完成，开始整文件校验，id={id:N}，bytes={incoming.Item.TotalBytes}");
        try
        {
            var target = await incoming.Receiver.FinishAsync(token, () =>
            {
                incoming.Item.Status = TransferStatus.Committing;
                _onTransfer(incoming.Item);
                SessionLog.Write("Transfer", $"整文件哈希校验通过，开始最终落盘，id={id:N}");
            });
            _incoming.TryRemove(id, out _);

            incoming.Item.LocalPath = target;
            incoming.Item.CompletedBytes = incoming.Item.TotalBytes;
            incoming.Item.Status = TransferStatus.Completed;
            _onTransfer(incoming.Item);
            SessionLog.Write("Transfer", $"文件校验并原子落盘完成，id={id:N}，target={target}");
            await Enqueue(WireMessageType.TransferComplete, 2, TransferWire.EncodeId(id));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            _incoming.TryRemove(id, out _);

            try { await incoming.Receiver.DisposeAsync(); } catch (Exception disposeFailure)
            {
                SessionLog.Write("Transfer", $"关闭校验失败的接收文件失败，id={id:N}", disposeFailure);
            }
            incoming.Item.Status = TransferStatus.Failed;
            _onTransfer(incoming.Item);
            SessionLog.Write("Transfer", $"文件完成校验失败，id={id:N}", failure);
            await TrySendFailureAsync(id, failure.Message);
        }
        finally { await incoming.Receiver.DisposeAsync(); }
    }

    private async Task HandleTransferFailureAsync(FileTransferFailure failure)
    {
        CancelMtp(failure.Id, TransferStatus.Failed);
        CancelOfferDecision(failure.Id);
        if (_pendingOffers.TryRemove(failure.Id, out var pending))
        {
            pending.Item.Status = TransferStatus.Failed;
            pending.Item.FailureDetail = failure.Reason;
            _onTransfer(pending.Item);
        }
        var exception = new RemoteTransferException(string.IsNullOrWhiteSpace(failure.Reason)
            ? "对端报告文件传输失败"
            : $"对端报告文件传输失败：{failure.Reason}");
        if (_outgoing.TryGetValue(failure.Id, out var outgoing)) outgoing.Fail(exception);
        if (_incoming.TryRemove(failure.Id, out var incoming))
        {

            try { await incoming.Receiver.DisposeAsync(); } catch (Exception disposeFailure)
            {
                SessionLog.Write("Transfer", $"关闭被取消的接收文件失败，id={failure.Id:N}", disposeFailure);
            }
            incoming.Item.FailureDetail = failure.Reason;
            incoming.Progress.Report(TransferStatus.Failed);
        }
        SessionLog.Write("Transfer", $"收到对端失败通知，id={failure.Id:N}，reason={failure.Reason}");
    }

    private async Task TrySendFailureAsync(Guid id, string reason)
    {
        if (_cancellation.IsCancellationRequested || _keys is null) return;
        try
        {
            await Enqueue(WireMessageType.TransferFailed, 2,
                TransferWire.EncodeFailure(new(id, string.IsNullOrWhiteSpace(reason) ? "文件传输失败" : reason)));
        }
        catch (Exception failure)
        {
            SessionLog.Write("Transfer", $"发送失败通知失败，id={id:N}", failure);
        }
    }

    private void EnsureTrusted(SessionKeys keys)
    {
        if (_identityStore.MatchesTrustedKey(keys.RemotePeerId, keys.RemoteIdentityPublicKey) != true)
            throw new TrustHandshakeException(TrustStage.Revoked, "设备身份或信任关系已变化，请重新连接。");
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0 && _connection.Transport == TransportKind.Usb &&
            _keys is not null && _writer is not null && Volatile.Read(ref _disposed) == 0)
        {
            try { await Enqueue(WireMessageType.GoAway, 0, []).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { /* A silent or removed peer is aborted below; shutdown remains bounded. */ }
        }
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _onClosed("设备会话已结束");
        lock (_receiveDecisionLock) if (!_cancellation.IsCancellationRequested) _cancellation.Cancel();
        var mtpStopped = _mtpJobs.Values.Select(job => job.Stopped.Task).ToArray();
        DropMtp();
        lock (_outboundLock)
            while (_outbound.TryDequeue(out var pending, out _)) pending.Completion.TrySetCanceled();
        if (_writer is not null)
        {
            try { await _writer; } catch (OperationCanceledException) { } catch { }
        }
        if (_heartbeat is not null) await _heartbeat;
        foreach (var transfer in _incoming.Values)
        {
            try { await transfer.Receiver.DisposeAsync(); }
            catch (Exception failure)
            {
                SessionLog.Write("Transfer", $"会话结束时关闭接收文件失败，id={transfer.Offer.Id:N}", failure);
            }
        }
        _incoming.Clear();

        var stopped = new OperationCanceledException("设备会话已结束");
        foreach (var transfer in _outgoing.Values) transfer.Fail(stopped);
        _outgoing.Clear();
        await _connection.DisposeAsync();
        await Task.WhenAll(mtpStopped);
        _outboundSignal.Dispose();
        // RunAsync and confirmation cancellation may still observe this token while the transport closes.
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }

    private static string MimeTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private sealed record Outbound(WireMessageType Type, int Stream, byte[] Payload, TaskCompletionSource Completion);
    private sealed record IncomingTransfer(FileOffer Offer, TransferItem Item, TransferReceiver Receiver, Action<TransferItem> Notify)
    {
        public TransferPauseController Progress { get; set; } = new(Item, Notify);
    }
    private sealed record PendingOffer(FileOffer Offer, TransferItem Item, DateTimeOffset ExpiresAt);
    private sealed class RemoteTransferException(string message) : IOException(message);

    private sealed class OutgoingTransfer(TransferItem item, Action<TransferItem> notify)
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _extentAcknowledgements = new();
        public TransferPauseController Progress { get; } = new(item, notify);
        public TransferItem Item { get; } = item;
        public TaskCompletionSource<int> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExpectExtent(int index)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_extentAcknowledgements.TryAdd(index, completion))
                throw new InvalidOperationException($"数据块 {index} 已在等待确认");
            return completion;
        }

        public void AcknowledgeExtent(int index)
        {
            if (_extentAcknowledgements.TryGetValue(index, out var completion)) completion.TrySetResult();
        }

        public void RemoveExtent(int index) => _extentAcknowledgements.TryRemove(index, out _);

        public void Fail(Exception failure)
        {
            Progress.Fail(failure);
            Accepted.TrySetException(failure);
            Completed.TrySetException(failure);
            foreach (var acknowledgement in _extentAcknowledgements.Values)
                acknowledgement.TrySetException(failure);
        }


    }
}
