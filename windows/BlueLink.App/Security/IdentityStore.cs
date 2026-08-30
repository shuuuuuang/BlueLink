using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace BlueLink.Security;

public sealed record TrustedIdentity(string PeerIdHex, byte[] PublicKey);

public sealed class DeviceIdentity
{
    private static readonly byte[] Ed25519X509Prefix = Convert.FromHexString("302A300506032B6570032100");
    internal Ed25519PrivateKeyParameters PrivateKey { get; }
    public byte[] PublicKeyRaw { get; }
    public byte[] PublicKey => [.. Ed25519X509Prefix, .. PublicKeyRaw];
    public byte[] PeerId => SHA256.HashData(PublicKey)[..16];

    private DeviceIdentity(Ed25519PrivateKeyParameters privateKey)
    {
        PrivateKey = privateKey;
        PublicKeyRaw = privateKey.GeneratePublicKey().GetEncoded();
    }

    public static DeviceIdentity Generate() => new(new Ed25519PrivateKeyParameters(new SecureRandom()));
    public static DeviceIdentity Restore(byte[] privateKey) => new(new Ed25519PrivateKeyParameters(privateKey, 0));
    public byte[] ExportPrivateKey() => PrivateKey.GetEncoded();

    public byte[] Sign(byte[] content)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, PrivateKey);
        signer.BlockUpdate(content, 0, content.Length);
        return signer.GenerateSignature();
    }

    public static void Verify(byte[] publicKey, byte[] peerId, byte[] content, byte[] signature)
    {
        if (publicKey.Length != 44 || !publicKey.AsSpan(0, 12).SequenceEqual(Ed25519X509Prefix))
            throw new CryptographicException("Unsupported Ed25519 public key encoding");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(publicKey)[..16], peerId))
            throw new CryptographicException("Peer id mismatch");
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 12));
        signer.BlockUpdate(content, 0, content.Length);
        if (!signer.VerifySignature(signature)) throw new CryptographicException("Invalid identity signature");
    }
}

public sealed class IdentityStore
{
    private readonly string _path;
    private readonly StoreData _data;
    public DeviceIdentity Identity { get; }
    public bool RecoveredCorruptIdentity { get; private set; }
    public string? RecoveryBackupPath { get; private set; }
    public IReadOnlyList<TrustedIdentity> TrustedIdentities => _data.Trusted
        .Select(pair => new TrustedIdentity(pair.Key, Convert.FromBase64String(pair.Value)))
        .ToArray();

    public IdentityStore(string? dataRoot = null)
    {
        var directory = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "identity.json");
        _data = LoadStoreData(directory);
        byte[]? protectedKey = null;
        byte[]? legacyKey = null;
        try
        {
            protectedKey = string.IsNullOrWhiteSpace(_data.PrivateKeyProtected)
                ? null : WindowsDataProtection.Unprotect(Convert.FromBase64String(_data.PrivateKeyProtected));
            legacyKey = string.IsNullOrWhiteSpace(_data.PrivateKey)
                ? null : Convert.FromBase64String(_data.PrivateKey);
            if (protectedKey is { Length: not 32 } || legacyKey is { Length: not 32 })
                throw new CryptographicException("The stored Ed25519 identity has an invalid length.");
        }
        catch (Exception failure) when (failure is CryptographicException or FormatException)
        {
            RecoverIdentity(directory, failure);
            protectedKey = null;
            legacyKey = null;
        }
        Identity = protectedKey is not null
            ? DeviceIdentity.Restore(protectedKey)
            : legacyKey is not null ? DeviceIdentity.Restore(legacyKey) : DeviceIdentity.Generate();
        if (protectedKey is null)
        {
            _data.PrivateKeyProtected = Convert.ToBase64String(
                WindowsDataProtection.Protect(Identity.ExportPrivateKey()));
            _data.PrivateKey = "";
            Save();
        }
    }

    private StoreData LoadStoreData(string directory)
    {
        if (!File.Exists(_path)) return new StoreData();
        try
        {
            return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path)) ?? new StoreData();
        }
        catch (JsonException failure)
        {
            var recovered = new StoreData();
            BackupUnreadableIdentity(directory, failure);
            return recovered;
        }
    }

    private void RecoverIdentity(string directory, Exception failure)
    {
        BackupUnreadableIdentity(directory, failure);
        _data.PrivateKeyProtected = "";
        _data.PrivateKey = "";
        // A regenerated local identity changes this device's peer id. Requiring
        // trust confirmation again avoids silently carrying asymmetric trust.
        _data.Trusted.Clear();
    }

    private void BackupUnreadableIdentity(string directory, Exception failure)
    {
        RecoveredCorruptIdentity = true;
        try
        {
            if (File.Exists(_path))
            {
                RecoveryBackupPath = Path.Combine(directory,
                    $"identity.recovery-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");
                File.Copy(_path, RecoveryBackupPath, overwrite: false);
            }
            File.AppendAllText(Path.Combine(directory, "identity-recovery.log"),
                $"[{DateTimeOffset.Now:O}] {failure.GetType().Name}: {failure.Message}{Environment.NewLine}");
        }
        catch
        {
            // Recovery must not turn an unreadable old identity into a startup crash.
        }
    }

    public bool? MatchesTrustedKey(byte[] peerId, byte[] publicKey)
    {
        if (!_data.Trusted.TryGetValue(Convert.ToHexString(peerId), out var saved)) return null;
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(saved), publicKey);
    }

    public void ClearTrustedIdentities()
    {
        _data.Trusted.Clear();
        Save();
    }

    public void Trust(byte[] peerId, byte[] publicKey)
    {
        _data.Trusted[Convert.ToHexString(peerId)] = Convert.ToBase64String(publicKey);
        Save();
    }

    public void RemoveTrust(string peerIdHex)
    {
        if (_data.Trusted.Remove(peerIdHex)) Save();
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }

    private sealed class StoreData
    {
        public string PrivateKey { get; set; } = "";
        public string PrivateKeyProtected { get; set; } = "";
        public Dictionary<string, string> Trusted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static class WindowsDataProtection
    {
        private const int CryptProtectUiForbidden = 0x1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlueLink.Identity.v1");

        public static byte[] Protect(byte[] value) => Transform(value, protect: true);
        public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

        private static byte[] Transform(byte[] value, bool protect)
        {
            using var input = Blob.From(value);
            using var entropy = Blob.From(Entropy);
            DataBlob output;
            var succeeded = protect
                ? CryptProtectData(ref input.Value, "BlueLink device identity", ref entropy.Value,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input.Value, IntPtr.Zero, ref entropy.Value,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Size; public IntPtr Data; }

        private sealed class Blob : IDisposable
        {
            public DataBlob Value;
            public static Blob From(byte[] value)
            {
                var result = new Blob { Value = new DataBlob { Size = value.Length,
                    Data = Marshal.AllocHGlobal(value.Length) } };
                Marshal.Copy(value, 0, result.Value.Data, value.Length);
                return result;
            }
            public void Dispose()
            {
                if (Value.Data == IntPtr.Zero) return;
                var zeros = new byte[Value.Size];
                Marshal.Copy(zeros, 0, Value.Data, zeros.Length);
                Marshal.FreeHGlobal(Value.Data);
                Value.Data = IntPtr.Zero;
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description,
            ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
            ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
