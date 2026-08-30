using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BlueLink.Shared
{
    internal static class InstalledApplicationController
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        internal static bool IsRunning(string installFolder)
        {
            return FindOwnedProcesses(installFolder).Count != 0;
        }

        internal static bool Stop(string installFolder, TimeSpan gracefulTimeout,
            TimeSpan forcedTimeout, out string error)
        {
            error = null;
            var processes = FindOwnedProcesses(installFolder);
            if (processes.Count == 0) return true;

            try
            {
                WindowsAppControlChannel.SignalExit(installFolder);
                foreach (var process in processes)
                {
                    try { process.CloseMainWindow(); }
                    catch { }
                }

                var gracefulDeadline = DateTime.UtcNow + gracefulTimeout;
                foreach (var process in processes)
                {
                    var remaining = gracefulDeadline - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        try { process.WaitForExit((int)Math.Min(int.MaxValue, remaining.TotalMilliseconds)); }
                        catch { }
                    }
                }

                foreach (var process in processes.Where(IsAlive))
                {
                    try { process.Kill(); }
                    catch (Exception failure)
                    {
                        error = "无法终止蓝联进程 " + process.Id + "：" + failure.Message;
                        return false;
                    }
                }

                var forcedDeadline = DateTime.UtcNow + forcedTimeout;
                // A WPF process can report HasExited before it disappears
                // from the system process snapshot. Poll the exact owned paths
                // through the forced deadline so that this short teardown
                // state is not misreported as a failed shutdown.
                while (DateTime.UtcNow < forcedDeadline)
                {
                    if (!IsRunning(installFolder)) return true;
                    System.Threading.Thread.Sleep(100);
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }

            if (IsRunning(installFolder))
            {
                error = "蓝联仍在运行。请保存当前操作并手动退出后重试。";
                return false;
            }

            return true;
        }

        internal static IReadOnlyList<Process> FindOwnedProcesses(string installFolder)
        {
            var result = new List<Process>();
            if (string.IsNullOrWhiteSpace(installFolder)) return result;

            string root;
            try { root = Path.GetFullPath(installFolder); }
            catch { return result; }
            var targets = new[]
            {
                Path.Combine(root, "BlueLink.exe"),
                Path.Combine(root, "app", "BlueLink.exe"),
            };

            foreach (var process in Process.GetProcessesByName("BlueLink"))
            {
                try
                {
                    var executable = GetExecutablePath(process.Id);
                    if (targets.Any(target => PathsEqual(target, executable))) result.Add(process);
                    else process.Dispose();
                }
                catch
                {
                    process.Dispose();
                }
            }

            return result;
        }

        private static bool IsAlive(Process process)
        {
            try { return !process.HasExited; }
            catch { return false; }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return Path.GetFullPath(left).Equals(Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string GetExecutablePath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var capacity = 32768;
                var buffer = new StringBuilder(capacity);
                return QueryFullProcessImageName(handle, 0, buffer, ref capacity)
                    ? buffer.ToString()
                    : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            StringBuilder executablePath, ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
