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

namespace BlueLink.Session;

public sealed class PeerSession : IAsyncDisposable
{
    private const int FileExtentSize = 64 * 1024;
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExtentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);
    private readonly RfcommConnection _connection;
    private readonly bool _listenerRole;
    private readonly IdentityStore _identityStore;
    private readonly Func<string, string, string, Task<bool>> _confirmTrust;
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
    private readonly ConcurrentDictionary<Guid, OutgoingTransfer> _outgoing = new();
    private readonly Dictionary<Guid, IncomingTransfer> _incoming = new();
    private readonly ConcurrentDictionary<Guid, PendingOffer> _pendingOffers = new();
    private readonly ConcurrentDictionary<Guid, byte> _pausedIncoming = new();
    private long _order;
    private long _sendSequence;
    private SessionKeys? _keys;
    private BtxNegotiation _negotiation = new(1, 0, BtxCapability.None);
    private Task? _writer;
    private int _disposed;

    public PeerSession(RfcommConnection connection, bool listenerRole, IdentityStore identityStore,
        Func<string, string, string, Task<bool>> confirmTrust, Action<ChatItem> onMessage,
        Action<TransferItem> onTransfer, Action<string> onReady, Action<string> onClosed,
        Action<ChatEnvelope, bool>? onEnvelope = null, Action<ChatReceipt>? onReceipt = null,
        string? receiveDirectory = null, long maxReceiveBytes = long.MaxValue,
        bool autoAcceptFiles = true)
    {
        _connection = connection;
        _listenerRole = listenerRole;
        _identityStore = identityStore;
        _confirmTrust = confirmTrust;
        _onMessage = onMessage;
        _onTransfer = onTransfer;
        _onReady = onReady;
        _onClosed = onClosed;
        _onEnvelope = onEnvelope;
        _onReceipt = onReceipt;
        _receiveDirectory = string.IsNullOrWhiteSpace(receiveDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Received")
            : receiveDirectory;
        _maxReceiveBytes = maxReceiveBytes;
        _autoAcceptFiles = autoAcceptFiles;
    }

    public string ReceiveDirectory { get => _receiveDirectory; set => _receiveDirectory = value; }
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
            _keys = await HandshakeAsync(_cancellation.Token);
            stage = "协议协商";
            _writer = WriterLoopAsync(_cancellation.Token);
            await Enqueue(WireMessageType.ProtocolHello, 0, ProtocolGreeting.Current.Encode());
            var replay = new ReplayGuard();
            var remoteHello = await BtxRecordCodec.ReadAsync(_connection.Input, _keys.ReceiveKey,
                _keys.ReceiveNoncePrefix, replay, _cancellation.Token);
            if (remoteHello.Type != WireMessageType.ProtocolHello)
                throw new InvalidDataException("对端未先发送 PROTOCOL_HELLO");
            _negotiation = ProtocolGreeting.Current.Negotiate(ProtocolGreeting.Decode(remoteHello.Payload));
            _onReady(Convert.ToHexString(_keys.RemotePeerId));
            SessionLog.Write("Session", $"安全会话已建立，peer={_connection.PeerName}，BTX={_negotiation.Major}.{_negotiation.Minor}，caps=0x{(uint)_negotiation.Capabilities:X}");
            stage = "已连接会话";
            await ReadLoopAsync(replay, _cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
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
        finally { await DisposeAsync(); }
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

    public async Task SendFileAsync(string path)
    {
        var name = Path.GetFileName(path);
        var mimeType = MimeTypeFor(name);
        var snapshotId = Guid.NewGuid();
        var snapshotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueLink", "Cache", "Outgoing");
        var snapshotPath = await OutgoingSnapshot.CreateAsync(path, snapshotRoot, snapshotId, _cancellation.Token);
        SessionLog.Write("Transfer", $"已创建不可变发送快照，source={name}，snapshot={Path.GetFileName(snapshotPath)}");
        try
        {
        var structured = _negotiation.Supports(BtxCapability.StructuredMessages | BtxCapability.AttachmentMetadata);
        if (!structured)
        {
            var legacyId = Guid.NewGuid();
            var legacy = await DescribeAsync(snapshotPath, legacyId, name, mimeType, AttachmentRole.File, null, null);
            await SendPreparedTransferAsync(snapshotPath, legacy, path);
            return;
        }

        var messageId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
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
            Role = originalRole
        };
        _onTransfer(originalTransfer);
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
            if (preview is not null && previewAsset is not null)
            {
                try { await SendPreparedTransferAsync(previewAsset.Path, preview, previewAsset.Path); }
                catch (OperationCanceledException) { throw; }
                catch (Exception failure)
                {
                    SessionLog.Write("Transfer", $"图片预览发送失败，继续发送原图，id={preview.Id:N}", failure);
                }
            }
            await SendPreparedTransferAsync(snapshotPath, original, path, originalTransfer);
        }
        catch (Exception failure)
        {
            if (originalTransfer.Status == TransferStatus.Queued)
            {
                originalTransfer.Status = failure is OperationCanceledException
                    ? TransferStatus.Canceled : TransferStatus.Failed;
                originalTransfer.FailureDetail = failure.Message;
                _onTransfer(originalTransfer);
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
        }
    }

    public async Task RetryFileAsync(string path, TransferItem template)
    {
        if (!template.Outgoing || template.Id == Guid.Empty)
            throw new InvalidOperationException("仅可重试有效的发送任务");
        var snapshotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueLink", "Cache", "Outgoing");
        var snapshotPath = await OutgoingSnapshot.CreateAsync(path, snapshotRoot, template.Id, _cancellation.Token);
        try
        {
            var prepared = await DescribeAsync(snapshotPath, template.Id, template.Name, template.MimeType,
                template.Role, template.MessageId, template.AttachmentId);
            template.FailureDetail = null;
            await SendPreparedTransferAsync(snapshotPath, prepared, path, template);
        }
        finally
        {
            try { File.Delete(snapshotPath); }
            catch (Exception failure) { SessionLog.Write("Transfer", "清理重试快照失败，将由缓存清理器处理", failure); }
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
        TransferItem? existingTransfer = null)
    {
        var id = prepared.Id;
        var transfer = existingTransfer ?? new TransferItem
        {
            Id = id, Name = prepared.Name, TotalBytes = prepared.Size, Outgoing = true,
            Status = TransferStatus.Offered, MimeType = prepared.MimeType, LocalPath = localPath,
            MessageId = prepared.MessageId, AttachmentId = prepared.AttachmentId, Role = prepared.Role
        };
        transfer.Status = TransferStatus.Offered;
        _onTransfer(transfer);
        var state = new OutgoingTransfer(transfer);
        _outgoing[id] = state;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileExtentSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != prepared.Size) throw new IOException("待发送文件在准备后发生变化");
            SessionLog.Write("Transfer", $"发送 Offer，id={id:N}，name={prepared.Name}，bytes={prepared.Size}，role={prepared.Role}");
            var offer = new FileOffer(id, prepared.Name, prepared.Size, FileExtentSize, prepared.Hash,
                prepared.MessageId, prepared.AttachmentId, prepared.MimeType, prepared.Role);
            await Enqueue(WireMessageType.TransferOffer, 2, TransferWire.EncodeOffer(offer));
            var startIndex = await state.Accepted.Task.WaitAsync(OfferTimeout, _cancellation.Token);
            var startOffset = Math.Min(prepared.Size, (long)startIndex * FileExtentSize);
            if (startOffset > 0)
            {
                input.Position = startOffset;
                transfer.CompletedBytes = startOffset;
                transfer.Status = TransferStatus.Resuming;
                _onTransfer(transfer);
                SessionLog.Write("Transfer", $"从持久化断点继续，id={id:N}，extent={startIndex}，bytes={startOffset}");
            }

            var buffer = new byte[FileExtentSize];
            var index = startIndex;
            while (true)
            {
                await state.WaitIfPausedAsync(_cancellation.Token);
                var read = await ReadAtMostAsync(input, buffer, _cancellation.Token);
                if (read == 0) break;
                var data = buffer[..read];
                var acknowledgement = state.ExpectExtent(index);
                await Enqueue(WireMessageType.TransferExtent, 2,
                    TransferWire.EncodeExtent(new(id, index, SHA256.HashData(data), data)));
                await acknowledgement.Task.WaitAsync(ExtentTimeout, _cancellation.Token);
                state.RemoveExtent(index);
                index++;
                transfer.CompletedBytes += read;
                transfer.Status = TransferStatus.Transferring;
                _onTransfer(transfer);
            }
            await Enqueue(WireMessageType.TransferFinish, 2, TransferWire.EncodeId(id));
            transfer.Status = TransferStatus.Verifying;
            _onTransfer(transfer);
            await state.Completed.Task.WaitAsync(CompletionTimeout, _cancellation.Token);
            transfer.CompletedBytes = transfer.TotalBytes;
            transfer.Status = TransferStatus.Completed;
            _onTransfer(transfer);
            SessionLog.Write("Transfer", $"对端已校验并落盘，id={id:N}，bytes={prepared.Size}");
        }
        catch (Exception failure)
        {
            transfer.Status = TransferStatus.Failed;
            transfer.FailureDetail = failure.Message;
            _onTransfer(transfer);
            SessionLog.Write("Transfer", $"文件发送失败，id={id:N}，name={prepared.Name}", failure);
            if (failure is not RemoteTransferException)
                await TrySendFailureAsync(id, failure.Message);
            throw;
        }
        finally
        {
            _outgoing.TryRemove(id, out _);
            state.Fail(new OperationCanceledException("文件传输已结束"));
        }
    }

    private sealed record PreparedTransfer(Guid Id, string Name, string MimeType, long Size, byte[] Hash,
        AttachmentRole Role, Guid? MessageId, Guid? AttachmentId, AttachmentDescriptor Descriptor);

    public async Task CancelTransferAsync(Guid id, string reason = "用户取消")
    {
        var cancellation = new OperationCanceledException(reason);
        if (_outgoing.TryGetValue(id, out var outgoing)) outgoing.Fail(cancellation);
        if (_pendingOffers.TryRemove(id, out var pending))
        {
            pending.Item.Status = TransferStatus.Canceled;
            pending.Item.FailureDetail = reason;
            _onTransfer(pending.Item);
        }
        if (_incoming.Remove(id, out var incoming))
        {
            _pausedIncoming.TryRemove(id, out _);
            await incoming.Receiver.DisposeAsync();
            incoming.Item.Status = TransferStatus.Canceled;
            _onTransfer(incoming.Item);
        }
        if (_negotiation.Supports(BtxCapability.TransferControl))
            await Enqueue(WireMessageType.TransferControl, 0, TransferControlWire.Encode(new(id,
                TransferControlAction.Cancel, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason)));
        else await TrySendFailureAsync(id, reason);
    }

    public async Task PauseTransferAsync(Guid id)
    {
        if (_outgoing.TryGetValue(id, out var outgoing))
        {
            outgoing.Pause();
            outgoing.Item.Status = TransferStatus.Paused;
            _onTransfer(outgoing.Item);
        }
        if (_incoming.TryGetValue(id, out var incoming))
        {
            _pausedIncoming[id] = 0;
            incoming.Item.Status = TransferStatus.Paused;
            _onTransfer(incoming.Item);
        }
        if (_negotiation.Supports(BtxCapability.TransferControl))
            await Enqueue(WireMessageType.TransferControl, 0, TransferControlWire.Encode(new(id,
                TransferControlAction.Pause, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "用户暂停")));
    }

    public async Task ResumeTransferAsync(Guid id)
    {
        if (_outgoing.TryGetValue(id, out var outgoing))
        {
            outgoing.Item.Status = TransferStatus.Resuming;
            _onTransfer(outgoing.Item);
            outgoing.Resume();
        }
        if (_incoming.TryGetValue(id, out var incoming))
        {
            _pausedIncoming.TryRemove(id, out _);
            incoming.Item.Status = TransferStatus.Resuming;
            _onTransfer(incoming.Item);
        }
        if (_negotiation.Supports(BtxCapability.TransferControl))
            await Enqueue(WireMessageType.TransferControl, 0, TransferControlWire.Encode(new(id,
                TransferControlAction.Resume, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "用户继续")));
    }

    private async Task<SessionKeys> HandshakeAsync(CancellationToken token)
    {
        var local = HandshakeHello.Create(_identityStore.Identity);
        HandshakeHello remote;
        if (_listenerRole) { remote = await ReadHelloAsync(token); await WriteHelloAsync(local, token); }
        else { await WriteHelloAsync(local, token); remote = await ReadHelloAsync(token); }
        var keys = local.Derive(remote);
        switch (_identityStore.MatchesTrustedKey(keys.RemotePeerId, keys.RemoteIdentityPublicKey))
        {
            case false: throw new CryptographicException("已信任设备的身份密钥发生变化");
            case true: break;
            default:
                if (!await _confirmTrust(_connection.PeerName, keys.FormattedSafetyCode,
                        Convert.ToHexString(keys.RemotePeerId)))
                    throw new CryptographicException("用户未确认安全代码");
                _identityStore.Trust(keys.RemotePeerId, keys.RemoteIdentityPublicKey);
                break;
        }
        return keys;
    }

    private async Task WriteHelloAsync(HandshakeHello hello, CancellationToken token)
    {
        var encoded = hello.Encode();
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
        await _connection.Output.WriteAsync(length, token);
        await _connection.Output.WriteAsync(encoded, token);
        await _connection.Output.FlushAsync(token);
    }

    private async Task<HandshakeHello> ReadHelloAsync(CancellationToken token)
    {
        var length = new byte[4]; await _connection.Input.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is < 1 or > 4096) throw new InvalidDataException("握手消息长度无效");
        var value = new byte[size]; await _connection.Input.ReadExactlyAsync(value, token);
        return HandshakeHello.Decode(value);
    }

    private async Task WriterLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(token);
            Outbound item;
            lock (_outboundLock) item = _outbound.Dequeue();
            try
            {
                var keys = _keys ?? throw new InvalidOperationException("Session keys unavailable");
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
        var value = new Outbound(type, stream, payload, completion);
        lock (_outboundLock) _outbound.Enqueue(value, (WireMessagePriority.Of(type), _order++));
        _outboundSignal.Release();
        return completion.Task;
    }

    private async Task ReadLoopAsync(ReplayGuard replay, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var keys = _keys!;
            var frame = await BtxRecordCodec.ReadAsync(_connection.Input, keys.ReceiveKey, keys.ReceiveNoncePrefix, replay, token);
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
            }
        }
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
        if (control.Action == TransferControlAction.Pause)
        {
            if (_outgoing.TryGetValue(control.TransferId, out var paused))
            {
                paused.Pause();
                paused.Item.Status = TransferStatus.Paused;
                _onTransfer(paused.Item);
            }
            SessionLog.Write("Transfer", $"对端暂停文件传输，id={control.TransferId:N}");
            return;
        }
        if (control.Action == TransferControlAction.Resume)
        {
            if (_outgoing.TryGetValue(control.TransferId, out var resumed))
            {
                resumed.Item.Status = TransferStatus.Resuming;
                _onTransfer(resumed.Item);
                resumed.Resume();
            }
            SessionLog.Write("Transfer", $"对端继续文件传输，id={control.TransferId:N}");
            return;
        }
        if (control.Action != TransferControlAction.Cancel)
        {
            SessionLog.Write("Transfer", $"已收到暂不支持的控制指令，id={control.TransferId:N}，action={control.Action}");
            return;
        }
        var failure = new OperationCanceledException(string.IsNullOrWhiteSpace(control.Reason) ? "对端取消传输" : control.Reason);
        if (_outgoing.TryGetValue(control.TransferId, out var outgoing)) outgoing.Fail(failure);
        if (_incoming.Remove(control.TransferId, out var incoming))
        {
            _pausedIncoming.TryRemove(control.TransferId, out _);
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
            if (_incoming.ContainsKey(offer.Id) || _pendingOffers.ContainsKey(offer.Id))
                throw new InvalidDataException("重复文件 Offer");
            if (!_autoAcceptFiles) throw new InvalidDataException("对端已关闭自动接收文件");
            if (offer.Size > _maxReceiveBytes)
            {
                var pendingItem = CreateIncomingItem(offer, TransferStatus.Rejected);
                pendingItem.FailureDetail = $"超过当前接收限制（{_maxReceiveBytes} B）；30 秒内提高限制可继续接收";
                _pendingOffers[offer.Id] = new(offer, pendingItem, DateTimeOffset.UtcNow + OfferTimeout);
                _onTransfer(pendingItem);
                SessionLog.Write("Transfer", $"暂缓超限 Offer，id={offer.Id:N}，bytes={offer.Size}，limit={_maxReceiveBytes}");
                _ = ExpirePendingOfferAsync(offer.Id);
                return;
            }
            await AcceptOfferAsync(offer);
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

    private async Task AcceptOfferAsync(FileOffer offer, TransferItem? existing = null)
    {
        var safeName = Path.GetFileName(offer.Name);
        var normalized = offer with { Name = safeName };
        var root = offer.Role == AttachmentRole.ImagePreview
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueLink", "Cache", "Previews")
            : _receiveDirectory;
        var transfer = existing ?? CreateIncomingItem(offer, TransferStatus.Offered);
        transfer.FailureDetail = null;
        transfer.Status = TransferStatus.Offered;
        _incoming[offer.Id] = new(normalized, transfer, new TransferReceiver(root, normalized));
        _onTransfer(transfer);
        SessionLog.Write("Transfer", $"接受 Offer，id={offer.Id:N}，name={safeName}，bytes={offer.Size}，block={offer.ExtentSize}");
        var nextExtent = checked((int)(transfer.CompletedBytes / offer.ExtentSize));
        await Enqueue(WireMessageType.TransferAccept, 2, _negotiation.Supports(BtxCapability.ResumeState)
            ? TransferWire.EncodeAccept(new(offer.Id, nextExtent)) : TransferWire.EncodeId(offer.Id));
    }

    private async Task ReconsiderPendingOffersAsync()
    {
        if (!_autoAcceptFiles) return;
        foreach (var pair in _pendingOffers.ToArray())
        {
            if (pair.Value.Offer.Size > _maxReceiveBytes || pair.Value.ExpiresAt <= DateTimeOffset.UtcNow) continue;
            if (!_pendingOffers.TryRemove(pair.Key, out var pending)) continue;
            try { await AcceptOfferAsync(pending.Offer, pending.Item); }
            catch (Exception failure)
            {
                pending.Item.Status = TransferStatus.Failed;
                pending.Item.FailureDetail = failure.Message;
                _onTransfer(pending.Item);
                await TrySendFailureAsync(pair.Key, failure.Message);
            }
        }
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
            incoming.Item.CompletedBytes = incoming.Receiver.ContiguousBytes;
            incoming.Item.Status = _pausedIncoming.ContainsKey(extent.Id)
                ? TransferStatus.Paused : TransferStatus.Transferring;
            _onTransfer(incoming.Item);
            await Enqueue(WireMessageType.TransferExtentAck, 2,
                TransferWire.EncodeExtentAck(new(extent.Id, extent.Index)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            _incoming.Remove(extent.Id);
            _pausedIncoming.TryRemove(extent.Id, out _);
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
            _incoming.Remove(id);
            _pausedIncoming.TryRemove(id, out _);
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
            _incoming.Remove(id);
            _pausedIncoming.TryRemove(id, out _);
            try { await incoming.Receiver.DisposeAsync(); } catch (Exception disposeFailure)
            {
                SessionLog.Write("Transfer", $"关闭校验失败的接收文件失败，id={id:N}", disposeFailure);
            }
            incoming.Item.Status = TransferStatus.Failed;
            _onTransfer(incoming.Item);
            SessionLog.Write("Transfer", $"文件完成校验失败，id={id:N}", failure);
            await TrySendFailureAsync(id, failure.Message);
        }
    }

    private async Task HandleTransferFailureAsync(FileTransferFailure failure)
    {
        var exception = new RemoteTransferException(string.IsNullOrWhiteSpace(failure.Reason)
            ? "对端报告文件传输失败"
            : $"对端报告文件传输失败：{failure.Reason}");
        if (_outgoing.TryGetValue(failure.Id, out var outgoing)) outgoing.Fail(exception);
        if (_incoming.Remove(failure.Id, out var incoming))
        {
            _pausedIncoming.TryRemove(failure.Id, out _);
            try { await incoming.Receiver.DisposeAsync(); } catch (Exception disposeFailure)
            {
                SessionLog.Write("Transfer", $"关闭被取消的接收文件失败，id={failure.Id:N}", disposeFailure);
            }
            incoming.Item.Status = TransferStatus.Failed;
            _onTransfer(incoming.Item);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_cancellation.IsCancellationRequested) _cancellation.Cancel();
        if (_writer is not null)
        {
            try { await _writer; } catch (OperationCanceledException) { } catch { }
        }
        foreach (var transfer in _incoming.Values)
        {
            try { await transfer.Receiver.DisposeAsync(); }
            catch (Exception failure)
            {
                SessionLog.Write("Transfer", $"会话结束时关闭接收文件失败，id={transfer.Offer.Id:N}", failure);
            }
        }
        _incoming.Clear();
        _pausedIncoming.Clear();
        var stopped = new OperationCanceledException("蓝牙会话已结束");
        foreach (var transfer in _outgoing.Values) transfer.Fail(stopped);
        _outgoing.Clear();
        await _connection.DisposeAsync();
        _outboundSignal.Dispose();
        _cancellation.Dispose();
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
    private sealed record IncomingTransfer(FileOffer Offer, TransferItem Item, TransferReceiver Receiver);
    private sealed record PendingOffer(FileOffer Offer, TransferItem Item, DateTimeOffset ExpiresAt);
    private sealed class RemoteTransferException(string message) : IOException(message);

    private sealed class OutgoingTransfer(TransferItem item)
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _extentAcknowledgements = new();
        private readonly object _pauseGate = new();
        private TaskCompletionSource _resumeSignal = CompletedSignal();
        public TransferItem Item { get; } = item;
        public TaskCompletionSource<int> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Pause()
        {
            lock (_pauseGate)
                if (_resumeSignal.Task.IsCompleted)
                    _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            lock (_pauseGate) _resumeSignal.TrySetResult();
        }

        public Task WaitIfPausedAsync(CancellationToken token)
        {
            lock (_pauseGate) return _resumeSignal.Task.WaitAsync(token);
        }

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
            lock (_pauseGate) _resumeSignal.TrySetException(failure);
            Accepted.TrySetException(failure);
            Completed.TrySetException(failure);
            foreach (var acknowledgement in _extentAcknowledgements.Values)
                acknowledgement.TrySetException(failure);
        }

        private static TaskCompletionSource CompletedSignal()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult();
            return completion;
        }
    }
}
