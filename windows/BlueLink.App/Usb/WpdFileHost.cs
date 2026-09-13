using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BlueLink.Usb;

internal sealed record WpdBinding(string DeviceId, string FolderId, DeviceFileQueue Queue);

internal static class WpdFileHost
{
    private static readonly ConcurrentDictionary<string, DeviceFileQueue> Queues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceAccess = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Discovery = new(1, 1);
    public static Task<bool> PresentAsync(string id, CancellationToken token) => Worker(() => WpdDevice.Devices().Contains(id, StringComparer.OrdinalIgnoreCase), token);
    public static async Task<WpdBinding?> BindAsync(string[] path, byte[] proof, CancellationToken token, Action<string>? unavailable = null)
    {
        if (proof.Length != 32 || path.Length is < 1 or > 16 ||
            path.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200 || p is "." or ".." || p.IndexOfAny(['/', '\\', '\0', ':']) >= 0) ||
            !path[^1].StartsWith("bluelink-usb-", StringComparison.Ordinal)) throw new IOException("Invalid USB directory announcement");
        await Discovery.WaitAsync(token);
        try
        {
            return await Worker(() =>
            {
                var matches = new List<WpdBinding>();
                var pending = "等待 Windows 检测 MTP 设备";
                foreach (var id in WpdDevice.Devices())
                {
                    token.ThrowIfCancellationRequested();
                    var queue = Queues.GetOrAdd(id, _ => new());
                    if (queue.Count != 0) continue;
                    var access = DeviceAccess.GetOrAdd(id, _ => new(1, 1));
                    if (!access.Wait(0)) continue;
                    try
                    {
                        pending = "等待 Windows 开放 MTP 文件访问";
                        using var device = new WpdDevice(id);
                        pending = "等待手机公开 MTP 存储";
                        foreach (var storage in device.Children("DEVICE"))
                        {
                            string? folder = storage;
                            for (var depth = 0; depth < path.Length; depth++)
                            {
                                pending = $"等待手机 USB 第 {depth + 1} 层目录可见";
                                folder = device.Find(folder!, path[depth]);
                                if (folder is null) break;
                            }
                            if (folder is null) continue;
                            pending = "等待手机 USB 会话标记可读";
                            var marker = device.Find(folder, "peer-proof");
                            if (marker is null || !CryptographicOperations.FixedTimeEquals(device.ReadSmall(marker, 32), proof)) continue;
                            matches.Add(new(id, folder, queue));
                        }
                    }
                    catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException)
                    { pending += $" (0x{error.HResult:X8})"; }
                    finally { access.Release(); }
                }
                if (matches.Count > 1) throw new IOException("多个 USB 目录匹配同一认证会话，已停止自动选择");
                if (matches.Count == 0) unavailable?.Invoke(pending);
                return matches.SingleOrDefault();
            }, token);
        }
        finally { Discovery.Release(); }
    }
    public static async Task ExecuteAsync(WpdBinding binding, Action<WpdDevice> action, Action checkpoint, CancellationToken token)
    {
        var access = DeviceAccess.GetOrAdd(binding.DeviceId, _ => new(1, 1));
        await access.WaitAsync(token);
        try { await Worker(() =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var delay = 100;
            while (true)
            {
                token.ThrowIfCancellationRequested(); checkpoint();
                try { using var device = OpenBeforeTransfer(binding.DeviceId); action(device); return true; }
                // Operations must request retry only before opening/creating a data stream. A partial transfer is never replayed.
                catch (WpdBusyBeforeTransferException) when (DateTime.UtcNow < deadline)
                {
                    if (token.WaitHandle.WaitOne(delay)) token.ThrowIfCancellationRequested();
                    delay = Math.Min(1000, delay * 2);
                }
            }
        }, token); }
        finally { access.Release(); }
    }
    private static WpdDevice OpenBeforeTransfer(string id)
    {
        try { return new WpdDevice(id); }
        catch (COMException error) when (error.HResult == unchecked((int)0x800700AA)) { throw new WpdBusyBeforeTransferException(error); }
    }
    public static Task<T> Worker<T>(Func<T> action, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var hr = CoInitializeEx(IntPtr.Zero, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { return action(); } finally { CoUninitialize(); }
    }, token);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
internal sealed class WpdBusyBeforeTransferException(Exception inner) : IOException("USB 正被其他程序占用，等待可用", inner);
