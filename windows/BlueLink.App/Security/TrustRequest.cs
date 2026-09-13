using System.ComponentModel;
using System.Security.Cryptography;

namespace BlueLink.Security;

public enum TrustStage { Confirm, Waiting, Completed, Canceled, Rejected, TimedOut, IdentityChanged, RemoteClosed, Revoked, Failed }

public sealed class TrustRequest : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _abort = new();
    private TrustStage _stage;
    public string PeerName { get; }
    public IdentityCandidate? IdentityCandidate { get; }
    public Domain.PeerPlatform PeerPlatform { get; internal set; } = Domain.PeerPlatform.Unknown;
    public string PeerIcon => "/BlueLink;component/Assets/Figma/" + (PeerPlatform switch
    { Domain.PeerPlatform.Android => "phone.png", Domain.PeerPlatform.Windows => "desktop.png", _ => "generic.png" });
    public string SafetyCode { get; }
    public string LocalFingerprint { get; }
    public string RemoteFingerprint { get; }
    public string? TrustedFingerprint { get; }
    public string TransportAddress { get; internal set; } = "";
    public bool RetryAvailable { get; internal set; } = true;
    public TrustStage Stage { get { lock (_gate) return _stage; } }
    public bool RequiresConfirmation { get; }
    public Task<bool> Decision => _decision.Task;
    public CancellationToken Cancellation => _abort.Token;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title => IdentityCandidate is not null && Stage == TrustStage.Confirm
        ? Localization.Strings.Get("可能是原设备，身份已变化") : Localization.Strings.Get(Stage switch
    {
        TrustStage.Confirm => "确认安全连接", TrustStage.Waiting => "等待对方确认", TrustStage.Rejected => "连接请求被拒绝",
        TrustStage.TimedOut => "安全确认已超时", TrustStage.IdentityChanged => "设备身份已变化",
        TrustStage.Completed => "安全连接已建立", TrustStage.Revoked => "信任关系已变化",
        TrustStage.Canceled => "连接已取消", _ => "连接已关闭",
    });
    public string StatusLabel => Localization.Strings.Get(Stage is TrustStage.Confirm or TrustStage.Waiting ? "安全码" : "连接状态");
    public string StatusText => Stage is TrustStage.Confirm or TrustStage.Waiting ? SafetyCode : Localization.Strings.Get(Stage switch
    {
        TrustStage.Rejected => "对方已拒绝", TrustStage.TimedOut => "本次安全码已失效",
        TrustStage.IdentityChanged or TrustStage.Revoked => "已阻止连接", TrustStage.Completed => "已连接",
        TrustStage.Canceled => "已取消", _ => "未建立安全连接",
    });
    public double StatusLineHeight => Stage == TrustStage.Confirm ? 60 : 32;
    public double StatusFontSize => Stage == TrustStage.Confirm ? 48 : 24;
    public string Detail => IdentityCandidate is not null && Stage == TrustStage.Confirm
        ? Localization.Strings.Format($"连接线索与“{IdentityCandidate.DisplayName}”一致，请重新核对两端安全码。") : Localization.Strings.Get(Stage switch
    {
        TrustStage.Confirm => "请确认两台设备显示相同安全码",
        TrustStage.Waiting => "本机已确认，请等待对方核对安全码并确认。",
        TrustStage.Rejected => "未建立安全连接，也未新增信任。",
        TrustStage.TimedOut => "未建立信任，请重新连接并核对新安全码。",
        TrustStage.IdentityChanged => "本次指纹与已信任的设备指纹不一致。",
        TrustStage.Revoked => "本次确认已失效，请重新连接并核对安全码。",
        TrustStage.Completed => "已核对双方身份。",
        _ => "连接已结束；未完成的安全确认不会保存信任。",
    });
    public string FirstFingerprintLabel => Localization.Strings.Get(Stage == TrustStage.IdentityChanged || IdentityCandidate is not null ? "已信任指纹" : "本机指纹");
    public string SecondFingerprintLabel => Localization.Strings.Get(Stage == TrustStage.IdentityChanged || IdentityCandidate is not null ? "本次指纹" : "对端指纹");
    public string FirstFingerprint => Stage == TrustStage.IdentityChanged || IdentityCandidate is not null ? TrustedFingerprint ?? "—" : LocalFingerprint;
    public string Explanation => IdentityCandidate is not null && Stage is TrustStage.Confirm or TrustStage.Waiting
        ? Localization.Strings.Get("仅在确认是同一台设备且安全码一致时关联。确认后保留原会话和文件记录，替换旧身份的信任。") : Localization.Strings.Get(Stage switch
    {
        TrustStage.Confirm => "确认后将固定此设备身份；若设备密钥发生变化，后续连接会被拒绝。",
        TrustStage.Waiting => "双方确认前不会建立信任，也不会发送消息或文件。",
        TrustStage.Rejected => "请先与对方确认连接意愿，再重新发起连接。",
        TrustStage.TimedOut => "旧安全码不能再次使用；重连后会重新进行身份校验。",
        TrustStage.IdentityChanged => "请先核实设备身份。如确认是设备重置，请在信任管理中移除旧信任后重新配对。",
        _ => "重新连接时会生成新的会话密钥并重新检查设备身份。",
    });
    public string PrimaryText => IdentityCandidate is not null && Stage == TrustStage.Confirm
        ? Localization.Strings.Get("核对一致并关联") : Localization.Strings.Get(Stage switch
    {
        TrustStage.Confirm => "确认并信任", TrustStage.Waiting => "等待对方…",
        TrustStage.IdentityChanged => "信任管理", _ => RetryAvailable ? "重新连接" : "等待对方重连",
    });
    public string SecondaryText => Localization.Strings.Get(Stage == TrustStage.Confirm ? "取消" : Stage == TrustStage.Waiting ? "取消连接" : "关闭");
    public bool PrimaryEnabled => Stage is TrustStage.Confirm or TrustStage.IdentityChanged || RetryAvailable &&
        Stage is TrustStage.Rejected or TrustStage.TimedOut or TrustStage.RemoteClosed or TrustStage.Revoked or TrustStage.Failed;

    public TrustRequest(string peerName, string safetyCode, string localFingerprint,
        string remoteFingerprint, bool requiresConfirmation = true, string? trustedFingerprint = null, IdentityCandidate? identityCandidate = null)
    {
        PeerName = peerName;
        IdentityCandidate = identityCandidate;
        SafetyCode = safetyCode;
        LocalFingerprint = localFingerprint;
        RemoteFingerprint = remoteFingerprint;
        TrustedFingerprint = trustedFingerprint;
        RequiresConfirmation = requiresConfirmation;
        _stage = requiresConfirmation ? TrustStage.Confirm : TrustStage.Waiting;
        if (!requiresConfirmation) _decision.TrySetResult(true);
    }

    public void Confirm()
    {
        lock (_gate)
        {
            if (_stage != TrustStage.Confirm) return;
            _stage = TrustStage.Waiting;
            _decision.TrySetResult(true);
        }
        Notify();
    }

    public void Cancel()
    {
        bool abort;
        lock (_gate)
        {
            if (_stage is not (TrustStage.Confirm or TrustStage.Waiting)) return;
            abort = _stage == TrustStage.Waiting;
            _stage = TrustStage.Canceled;
            _decision.TrySetResult(false);
        }
        if (abort) _abort.Cancel();
        Notify();
    }

    public void Finish(TrustStage stage)
    {
        lock (_gate)
        {
            if (_stage is not (TrustStage.Confirm or TrustStage.Waiting)) return;
            _stage = stage;
            _decision.TrySetResult(false);
        }
        Notify();
    }

    internal bool TryComplete(Func<bool> commit)
    {
        lock (_gate)
        {
            if (_stage != TrustStage.Waiting || !commit()) return false;
            _stage = TrustStage.Completed;
        }
        Notify();
        return true;
    }

    private void Notify() => PropertyChanged?.Invoke(this, new(string.Empty));
    public static string Fingerprint(byte[] publicKey) => string.Join(":",
        Convert.ToHexString(SHA256.HashData(publicKey)).Chunk(4).Take(6).Select(chars => new string(chars)));
}
