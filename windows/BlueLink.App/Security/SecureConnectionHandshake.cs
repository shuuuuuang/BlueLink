using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BlueLink.Protocol;

namespace BlueLink.Security;

public sealed record SecureHandshakeResult(SessionKeys Keys, BtxNegotiation Negotiation, ReplayGuard Replay, string RemoteDeviceName = "");
public sealed class TrustHandshakeException(TrustStage stage, string message, Exception? inner = null) : IOException(message, inner)
{
    public TrustStage Stage { get; } = stage;
}

/// <summary>Authenticates the existing BTX hello before committing local trust; no user payload is sent here.</summary>
public static class SecureConnectionHandshake
{
    public static async Task<SecureHandshakeResult> RunAsync(Stream input, Stream output, bool listenerRole,
        IdentityStore identity, string peerName, Action<TrustRequest> present,
        CancellationToken cancellationToken, string? expectedTrustedPeerId = null, TimeSpan? timeout = null, IdentityAssociationHandler? association = null, string? localDeviceName = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var snapshot = identity.CaptureIdentity();
        TrustRequest? request = null;
        SessionKeys? keys = null;
        Task<BtxFrame>? remoteGreeting = null;
        var presented = false;
        long sendSequence = 0;
        try
        {
            var local = HandshakeHello.Create(snapshot.Identity);
            HandshakeHello remote;
            if (listenerRole) { remote = await ReadHelloAsync(input, token); await WriteHelloAsync(output, local, token); }
            else { await WriteHelloAsync(output, local, token); remote = await ReadHelloAsync(input, token); }
            keys = local.Derive(remote);
            var matches = identity.MatchesTrustedKey(keys.RemotePeerId, keys.RemoteIdentityPublicKey);
            var expectedKey = expectedTrustedPeerId is null ? null : identity.FindTrustedKey(expectedTrustedPeerId);
            var remoteId = Convert.ToHexString(keys.RemotePeerId);
            var candidate = matches is null && !identity.IsRetired(remoteId) && association is not null
                ? await association.Find(remoteId, token) : null;
            if (candidate is not null && !association!.CanAssociate(candidate))
                throw new TrustHandshakeException(TrustStage.Revoked, "原设备仍有活动会话或身份已变化，请断开后重试。");
            if (candidate is not null && (identity.IsRetired(candidate.PeerId) ||
                expectedTrustedPeerId is not null && !candidate.PeerId.Equals(expectedTrustedPeerId, StringComparison.OrdinalIgnoreCase))) candidate = null;
            var changed = identity.IsRetired(remoteId) || matches == false || expectedKey is not null && !CryptographicOperations.FixedTimeEquals(expectedKey, keys.RemoteIdentityPublicKey);
            var trustedKey = changed ? expectedKey ?? identity.FindTrustedKey(Convert.ToHexString(keys.RemotePeerId)) : null;
            request = new(peerName, keys.FormattedSafetyCode, TrustRequest.Fingerprint(snapshot.Identity.PublicKey),
                TrustRequest.Fingerprint(keys.RemoteIdentityPublicKey), matches != true || changed,
                candidate?.PublicKey is { } oldKey ? TrustRequest.Fingerprint(oldKey) : trustedKey is null ? null : TrustRequest.Fingerprint(trustedKey), candidate);
            if (changed && candidate is null)
            {
                request.Finish(TrustStage.IdentityChanged);
                present(request); presented = true;
                throw new TrustHandshakeException(TrustStage.IdentityChanged, "设备身份已变化，连接已被阻止。");
            }
            if (identity.RevocationVersion != snapshot.Version)
                throw new TrustHandshakeException(TrustStage.Revoked, "设备身份或信任关系已变化，请重新连接。");
            using var abort = request.Cancellation.Register(() => deadline.Cancel());
            if (request.RequiresConfirmation) { present(request); presented = true; }
            var replay = new ReplayGuard();
            remoteGreeting = BtxRecordCodec.ReadAsync(input, keys.ReceiveKey, keys.ReceiveNoncePrefix, replay, token);
            var decision = request.Decision.WaitAsync(token);
            // Observe an authenticated remote rejection even while local confirmation is still open.
            if (await Task.WhenAny(decision, remoteGreeting) == remoteGreeting) ValidateGreeting(await remoteGreeting);
            if (!await decision)
            {
                await WriteRejectionAsync(output, keys, sendSequence++, token);
                throw new TrustHandshakeException(TrustStage.Canceled, "本机取消了安全确认。");
            }
            token.ThrowIfCancellationRequested();
            await BtxRecordCodec.WriteAsync(output, new(WireMessageType.ProtocolHello, 0, 0, sendSequence++, DeviceNameGreeting.Encode(localDeviceName)),
                keys.SendKey, keys.SendNoncePrefix, token);
            var frame = await remoteGreeting;
            ValidateGreeting(frame);
            var negotiation = ProtocolGreeting.Current.Negotiate(ProtocolGreeting.Decode(frame.Payload));
            token.ThrowIfCancellationRequested();
            if (!request.TryComplete(() => !token.IsCancellationRequested && (candidate is null
                    ? identity.TryTrust(keys.RemotePeerId, keys.RemoteIdentityPublicKey, snapshot.Version)
                    : association!.CanAssociate(candidate) && identity.TryAssociate(keys.RemotePeerId, keys.RemoteIdentityPublicKey, snapshot.Version, candidate))))
                throw new TrustHandshakeException(TrustStage.Revoked, "设备身份或信任关系已变化，请重新连接。");
            if (candidate is not null) await association!.Apply();
            return new(keys, negotiation, replay, DeviceNameGreeting.Decode(frame.Payload));
        }
        catch (OperationCanceledException failure)
        {
            var stage = cancellationToken.IsCancellationRequested || request?.Stage == TrustStage.Canceled
                ? TrustStage.Canceled : TrustStage.TimedOut;
            request?.Finish(stage);
            if (request is not null && !presented && stage != TrustStage.Canceled) present(request);
            ClearKeys(keys);
            throw new TrustHandshakeException(stage, stage == TrustStage.TimedOut ? "安全确认已超时，请重新连接并核对新安全码。" : "连接已取消。", failure);
        }
        catch (Exception failure)
        {
            var stage = failure is TrustHandshakeException trustFailure ? trustFailure.Stage :
                failure is EndOfStreamException ? TrustStage.RemoteClosed : TrustStage.Failed;
            request?.Finish(stage);
            if (request is not null && !presented && stage != TrustStage.Canceled) present(request);
            ClearKeys(keys);
            throw failure is TrustHandshakeException ? failure : new TrustHandshakeException(stage,
                stage == TrustStage.RemoteClosed ? "对方关闭了连接，未新增信任。" : "安全连接失败，未新增信任。", failure);
        }
        finally
        {
            deadline.Cancel();
            // Observe pending reads without keeping a dead transport alive indefinitely.
            if (remoteGreeting is not null)
                _ = remoteGreeting.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static void ValidateGreeting(BtxFrame frame)
    {
        if (frame.Type == WireMessageType.GoAway)
            throw Encoding.UTF8.GetString(frame.Payload) == "TRUST_REJECTED"
                ? new TrustHandshakeException(TrustStage.Rejected, "对方拒绝了连接，未新增信任。")
                : new TrustHandshakeException(TrustStage.RemoteClosed, "对方关闭了连接，未新增信任。");
        if (frame.Type != WireMessageType.ProtocolHello)
            throw new InvalidDataException("对端未先发送 PROTOCOL_HELLO");
        _ = ProtocolGreeting.Current.Negotiate(ProtocolGreeting.Decode(frame.Payload));
    }

    private static Task WriteRejectionAsync(Stream output, SessionKeys keys, long sequence, CancellationToken token) =>
        BtxRecordCodec.WriteAsync(output, new(WireMessageType.GoAway, 0, 0, sequence, Encoding.UTF8.GetBytes("TRUST_REJECTED")),
            keys.SendKey, keys.SendNoncePrefix, token);

    private static void ClearKeys(SessionKeys? keys)
    {
        if (keys is null) return;
        CryptographicOperations.ZeroMemory(keys.SendKey);
        CryptographicOperations.ZeroMemory(keys.ReceiveKey);
    }

    private static async Task WriteHelloAsync(Stream output, HandshakeHello hello, CancellationToken token)
    {
        var encoded = hello.Encode();
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
        await output.WriteAsync(length, token);
        await output.WriteAsync(encoded, token);
        await output.FlushAsync(token);
    }

    private static async Task<HandshakeHello> ReadHelloAsync(Stream input, CancellationToken token)
    {
        var length = new byte[4]; await input.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is < 1 or > 4096) throw new InvalidDataException("握手消息长度无效");
        var value = new byte[size]; await input.ReadExactlyAsync(value, token);
        return HandshakeHello.Decode(value);
    }
}
