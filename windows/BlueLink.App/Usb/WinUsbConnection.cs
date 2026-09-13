using System.ComponentModel;
using System.Runtime.InteropServices;
using BlueLink.Domain;
using BlueLink.Transport;
using Microsoft.Win32.SafeHandles;

namespace BlueLink.Usb;

/// <summary>Bounded native I/O runs off the UI thread. Disposal aborts and drains every pipe before freeing handles.</summary>
public sealed class WinUsbConnection : IUsbHostConnection
{
    private readonly SafeFileHandle _file;
    private IntPtr _usb;
    private readonly byte _inputPipe;
    private readonly byte _outputPipe;
    private readonly SemaphoreSlim _readGate = new(1, 1), _writeGate = new(1, 1), _controlGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposing;
    public Task Closed => _closed.Task;
    public Stream Input { get; }
    public Stream Output { get; }
    public string PeerName { get; }
    public string TransportAddress { get; }
    public string? IdentityHint { get; }
    public bool ListenerRole => false;
    public TransportKind Transport => TransportKind.Usb;
    public PeerPlatform Platform => PeerPlatform.Android;
    public UsbLinkSpeed Speed { get; }

    public WinUsbConnection(UsbDevice device)
    {
        if (!device.AndroidCandidate || string.IsNullOrWhiteSpace(device.InterfacePath) || !device.Driver.Equals("WinUSB", StringComparison.OrdinalIgnoreCase))
            throw new UsbFailure(UsbStage.DriverMissing, "电脑端 USB 驱动未就绪");
        if (device.PolicyBlocked) throw new UsbFailure(UsbStage.PolicyBlocked, "USB 被系统策略阻止");
        PeerName = device.Name;
        TransportAddress = "usb:" + device.InstanceId;
        _file = CreateFile(device.InterfacePath, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (_file.IsInvalid) { var error = Marshal.GetLastWin32Error(); _file.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!WinUsb_Initialize(_file, out _usb)) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetTimeout(0, 5000);
            IdentityHint = ReadIdentityHint();
            if (device.AccessoryMode)
            {
                if (!WinUsb_QueryInterfaceSettings(_usb, 0, out var descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (descriptor.Number != 0 || descriptor.Class != 255 || descriptor.SubClass != 255 || descriptor.Protocol != 0)
                    throw new UsbFailure(UsbStage.Unsupported, "USB 接口不是 AOA 数据接口。");
                for (byte index = 0; index < descriptor.Endpoints; index++)
                {
                    if (!WinUsb_QueryPipe(_usb, 0, index, out var pipe)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (pipe.Type != 2) continue;
                    if ((pipe.Id & 0x80) != 0) _inputPipe = pipe.Id; else _outputPipe = pipe.Id;
                }
                if (_inputPipe == 0 || _outputPipe == 0) throw new UsbFailure(UsbStage.Unsupported, "USB 配件缺少 Bulk IN/OUT 通道。");
                SetTimeout(_inputPipe, 1000); SetTimeout(_outputPipe, 5000);
                DrainPreviousSession();
                var speed = new byte[1]; uint length = 1;
                if (WinUsb_QueryDeviceInformation(_usb, 1, ref length, speed))
                    Speed = speed[0] switch { 1 or 2 => UsbLinkSpeed.FullSpeed, 3 => UsbLinkSpeed.HighSpeed, _ => UsbLinkSpeed.Unknown };
            }
            Input = new PipeStream(this, true); Output = new PipeStream(this, false);
        }
        catch { if (_usb != IntPtr.Zero) WinUsb_Free(_usb); _usb = IntPtr.Zero; _file.Dispose(); throw; }
    }

    private string? ReadIdentityHint()
    {
        // Descriptor serial only. A port-derived PnP instance ID must never identify a physical peer.
        var device = new byte[18];
        if (!WinUsb_GetDescriptor(_usb, 1, 0, 0, device, 18, out var count) || count < 18 || device[1] != 1 || device[16] == 0) return null;
        var languages = new byte[255];
        if (!WinUsb_GetDescriptor(_usb, 3, 0, 0, languages, 255, out count) || count < 4 || languages[1] != 3) return null;
        var language = (ushort)(languages[2] | languages[3] << 8);
        var serial = new byte[255];
        if (!WinUsb_GetDescriptor(_usb, 3, device[16], language, serial, 255, out count) || count < 4 ||
            serial[1] != 3 || serial[0] < 4 || serial[0] > count || serial[0] % 2 != 0) return null;
        return Security.PeerIdentityHint.UsbSerial(System.Text.Encoding.Unicode.GetString(serial, 2, serial[0] - 2));
    }

    public async Task<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, byte[] bytes, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _controlGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                stop.Token.ThrowIfCancellationRequested();
                var packet = new SetupPacket { RequestType = requestType, Request = request, Value = value, Index = index, Length = checked((ushort)bytes.Length) };
                if (!WinUsb_ControlTransfer(_usb, packet, bytes, (uint)bytes.Length, out var transferred, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                stop.Token.ThrowIfCancellationRequested();
                return checked((int)transferred);
            }, stop.Token).ConfigureAwait(false);
        }
        finally { _controlGate.Release(); }
    }

    private async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token)
    {
        if (target.Length == 0) return 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _readGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var buffer = new byte[Math.Min(target.Length, 65536)];
            var count = await Task.Run(() =>
            {
                using var abort = stop.Token.Register(() => WinUsb_AbortPipe(_usb, _inputPipe));
                var emptyPackets = 0;
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var ok = WinUsb_ReadPipe(_usb, _inputPipe, buffer, (uint)buffer.Length, out var read, IntPtr.Zero);
                    var error = ok ? 0 : Marshal.GetLastWin32Error();
                    stop.Token.ThrowIfCancellationRequested();
                    if (ok && read > 0) return checked((int)read);
                    if (!ok && error == 121 && read == 0) continue; // Idle timeout, not EOF.
                    if (!ok) throw new Win32Exception(error);
                    if (++emptyPackets > 32) throw new IOException("USB 通道连续返回空数据。");
                }
            }, stop.Token).ConfigureAwait(false);
            buffer.AsMemory(0, count).CopyTo(target); return count;
        }
        finally { _readGate.Release(); }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _writeGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            while (!source.IsEmpty)
            {
                var buffer = source[..Math.Min(source.Length, 65536)].ToArray();
                await Task.Run(() =>
                {
                    using var abort = stop.Token.Register(() => WinUsb_AbortPipe(_usb, _outputPipe));
                    stop.Token.ThrowIfCancellationRequested();
                    if (!WinUsb_WritePipe(_usb, _outputPipe, buffer, (uint)buffer.Length, out var written, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error(); stop.Token.ThrowIfCancellationRequested(); throw new Win32Exception(error);
                    }
                    if (written != buffer.Length) throw new IOException("USB 数据未完整写入。");
                }, stop.Token).ConfigureAwait(false);
                source = source[buffer.Length..];
            }
        }
        finally { _writeGate.Release(); }
    }

    private void DrainPreviousSession()
    {
        // AOA remains electrically connected when the host process exits. Old encrypted
        // records may still be queued in the phone, beyond WinUSB's local read cache.
        // The Android listener sends nothing for a NEW session until our HELLO, so only
        // this pre-handshake boundary can safely drain them. Never resync an active BTX stream.
        SetTimeout(_inputPipe, 100);
        try
        {
            var buffer = new byte[65536];
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var ok = WinUsb_ReadPipe(_usb, _inputPipe, buffer, (uint)buffer.Length, out var count, IntPtr.Zero);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error == 121 && count == 0) return;
                if (!ok) throw new Win32Exception(error);
            }
            throw new UsbFailure(UsbStage.Unavailable, "旧 USB 通道仍在发送数据，请重新连接。");
        }
        finally { SetTimeout(_inputPipe, 1000); }
    }

    private void SetTimeout(byte pipe, uint milliseconds)
    {
        if (!WinUsb_SetPipePolicy(_usb, pipe, 3, 4, ref milliseconds)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) { await _closed.Task.ConfigureAwait(false); return; }
        _lifetime.Cancel();
        try
        {
            await _readGate.WaitAsync().ConfigureAwait(false);
            await _writeGate.WaitAsync().ConfigureAwait(false);
            await _controlGate.WaitAsync().ConfigureAwait(false);
            if (_usb != IntPtr.Zero) { WinUsb_Free(_usb); _usb = IntPtr.Zero; }
            _file.Dispose();
        }
        finally
        {
            _readGate.Release(); _writeGate.Release(); _controlGate.Release();
            _closed.TrySetResult();
        }
    }

    private sealed class PipeStream(WinUsbConnection owner, bool reading) : Stream
    {
        public override bool CanRead => reading;
        public override bool CanWrite => !reading;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => reading ? owner.ReadAsync(buffer, token) : throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => !reading ? owner.WriteAsync(buffer, token) : throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct SetupPacket { public byte RequestType, Request; public ushort Value, Index, Length; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct InterfaceDescriptor { public byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, Description; }
    [StructLayout(LayoutKind.Sequential)] private struct PipeInfo { public int Type; public byte Id; public ushort MaximumPacketSize; public byte Interval; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
    [DllImport("winusb.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_GetDescriptor(IntPtr usb, byte type, byte index, ushort language, [Out] byte[] buffer, uint length, out uint transferred);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alternate, out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out PipeInfo pipe);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_QueryDeviceInformation(IntPtr usb, uint type, ref uint length, byte[] buffer);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_ControlTransfer(IntPtr usb, SetupPacket packet, [In, Out] byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_AbortPipe(IntPtr usb, byte pipe);
}
