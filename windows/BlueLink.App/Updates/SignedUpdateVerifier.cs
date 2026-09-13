using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueLink.Updates;

/// <summary>An update must be signed by the same publisher key as this installed client.</summary>
public sealed class SignedUpdateVerifier : IUpdatePackageVerifier
{
    public void Verify(string path, Version version)
    {
        using var info = new TrustFile(path);
        using var data = new TrustData(info);
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        if (WinVerifyTrust(new IntPtr(-1), action, data) != 0)
            throw new InvalidDataException("安装包没有有效的发布者签名，已阻止安装。");
        try
        {
            using var candidate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var installed = new X509Certificate2(X509Certificate.CreateFromSignedFile(typeof(SignedUpdateVerifier).Assembly.Location));
            if (!CryptographicOperations.FixedTimeEquals(candidate.PublicKey.ExportSubjectPublicKeyInfo(), installed.PublicKey.ExportSubjectPublicKeyInfo()))
                throw new InvalidDataException("安装包发布者与当前应用不一致，已阻止安装。");
        }
        catch (CryptographicException error) { throw new InvalidDataException("当前应用缺少可核验的发布者签名，无法自动安装更新。", error); }
        var product = FileVersionInfo.GetVersionInfo(path);
        if (!(product.ProductName?.Contains("BlueLink", StringComparison.OrdinalIgnoreCase) ?? false) ||
            !Version.TryParse(product.ProductVersion?.Split('+')[0], out var actual) || actual.Major != version.Major || actual.Minor != version.Minor || actual.Build != version.Build)
            throw new InvalidDataException("安装包产品或版本与发布信息不一致。");
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, TrustData data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class TrustFile : IDisposable
    {
        public uint Size = (uint)Marshal.SizeOf<TrustFile>();
        public IntPtr Path;
        public IntPtr File = IntPtr.Zero;
        public IntPtr Subject = IntPtr.Zero;
        public TrustFile(string path) => Path = Marshal.StringToCoTaskMemUni(path);
        public void Dispose() { Marshal.FreeCoTaskMem(Path); Path = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class TrustData : IDisposable
    {
        public uint Size = (uint)Marshal.SizeOf<TrustData>();
        public IntPtr Callback = IntPtr.Zero;
        public IntPtr Client = IntPtr.Zero;
        public uint UiChoice = 2; // WTD_UI_NONE: never display certificate dialogs.
        public uint Revocation = 0;
        public uint Choice = 1;
        public IntPtr File;
        public uint StateAction = 0;
        public IntPtr State = IntPtr.Zero;
        public IntPtr Url = IntPtr.Zero;
        public uint ProviderFlags = 0x00001000; // Cache-only verification, no blocking revocation network requests.
        public uint Context = 0;
        public TrustData(TrustFile file) { File = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>()); Marshal.StructureToPtr(file, File, false); }
        public void Dispose() { Marshal.FreeCoTaskMem(File); File = IntPtr.Zero; }
    }
}
