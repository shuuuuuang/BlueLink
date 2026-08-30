namespace BlueLink.Launcher
{
    using System;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography.X509Certificates;

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        public static void VerifyMicrosoft(string path)
        {
            var file = new WinTrustFileInfo(path);
            var data = new WinTrustData(file);
            try
            {
                var status = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, data);
                if (status != 0) throw new InvalidOperationException("运行时安装包的 Authenticode 签名无效（0x" + status.ToString("X8") + "）。");
                var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (certificate.Subject.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("运行时安装包不是 Microsoft 签名。");
            }
            finally
            {
                data.Dispose();
                file.Dispose();
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            public IntPtr pcwszFilePath;
            public IntPtr hFile = IntPtr.Zero;
            public IntPtr pgKnownSubject = IntPtr.Zero;
            public WinTrustFileInfo(string path) { pcwszFilePath = Marshal.StringToCoTaskMemUni(path); }
            public void Dispose() { if (pcwszFilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(pcwszFilePath); pcwszFilePath = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
            public IntPtr pPolicyCallbackData = IntPtr.Zero;
            public IntPtr pSIPClientData = IntPtr.Zero;
            public uint dwUIChoice = 2;
            public uint fdwRevocationChecks = 0;
            public uint dwUnionChoice = 1;
            public IntPtr pFile;
            public uint dwStateAction = 0;
            public IntPtr hWVTStateData = IntPtr.Zero;
            public IntPtr pwszURLReference = IntPtr.Zero;
            public uint dwProvFlags = 0x00001000;
            public uint dwUIContext = 0;
            public WinTrustData(WinTrustFileInfo file) { pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf(file)); Marshal.StructureToPtr(file, pFile, false); }
            public void Dispose() { if (pFile != IntPtr.Zero) Marshal.FreeCoTaskMem(pFile); pFile = IntPtr.Zero; }
        }
    }
}
