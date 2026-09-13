using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace BlueLink.Shared
{
    internal static class WindowsAppControlChannel
    {
        private const string Prefix = @"Local\BlueLink.Desktop.Control.";

        internal static string ExitEventName(string installFolder) =>
            Prefix + UserKey + "." + LocationKey(installFolder) + ".Exit";
        internal static string ShowEventName(string installFolder) =>
            Prefix + UserKey + "." + LocationKey(installFolder) + ".Show";
        internal static string InstanceMutexName(string installFolder) =>
            Prefix + UserKey + "." + LocationKey(installFolder) + ".Instance";

        internal static bool SignalExit(string installFolder) => Signal(ExitEventName(installFolder));
        internal static bool SignalShow(string installFolder) => Signal(ShowEventName(installFolder));

        internal static string CurrentInstallRoot()
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            var folder = Path.GetDirectoryName(executable) ?? AppDomain.CurrentDomain.BaseDirectory;
            var directory = new DirectoryInfo(Path.GetFullPath(folder));
            if (File.Exists(Path.Combine(directory.FullName, "BlueLink.portable"))) return directory.FullName;
            return directory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && directory.Parent != null
                ? directory.Parent.FullName
                : directory.FullName;
        }

        private static string UserKey
        {
            get
            {
                try
                {
                    var sid = WindowsIdentity.GetCurrent()?.User?.Value;
                    if (!string.IsNullOrWhiteSpace(sid)) return sid;
                }
                catch
                {
                    // A per-session event remains a safe fallback when the SID
                    // cannot be resolved in a restricted process.
                }

                return Environment.UserName.Replace('\\', '_').Replace('/', '_');
            }
        }

        private static bool Signal(string eventName)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(eventName))
                {
                    return signal.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string LocationKey(string installFolder)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(installFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                normalized = installFolder ?? String.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(24);
                for (var index = 0; index < 12; index++) builder.Append(bytes[index].ToString("X2"));
                return builder.ToString();
            }
        }
    }
}
