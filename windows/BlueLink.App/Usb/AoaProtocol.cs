using System.Buffers.Binary;
using System.Text;

namespace BlueLink.Usb;

public interface IUsbControlPipe
{
    Task<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, byte[] bytes, CancellationToken token);
}

public static class AoaProtocol
{
    // AOA endpoint-zero requests identify the BlueLink accessory declared by the Android app.
    public static async Task StartAccessoryAsync(IUsbControlPipe pipe, string serial, CancellationToken token)
    {
        var protocol = new byte[2];
        var count = await pipe.ControlAsync(0xC0, 51, 0, 0, protocol, token).ConfigureAwait(false);
        if (count != 2 || BinaryPrimitives.ReadUInt16LittleEndian(protocol) is not (1 or 2))
            throw new UsbFailure(UsbStage.Unsupported, "设备不支持 USB 配件模式");
        string[] fields = ["BlueLink", "BlueLink", "BlueLink secure local transfer", "1.0", "https://github.com/shuuuuuang/BlueLink", serial];
        for (ushort index = 0; index < fields.Length; index++)
        {
            if (fields[index].Contains('\0')) throw new InvalidDataException("AOA identification contains a null character.");
            var bytes = Encoding.UTF8.GetBytes(fields[index] + "\0");
            if (bytes.Length > 256) throw new InvalidDataException("AOA identification exceeds 256 bytes.");
            if (await pipe.ControlAsync(0x40, 52, 0, index, bytes, token).ConfigureAwait(false) != bytes.Length)
                throw new IOException("USB 配件识别信息未完整发送。");
        }
        await pipe.ControlAsync(0x40, 53, 0, 0, [], token).ConfigureAwait(false);
    }

    public static bool IsAccessory(string hardwareId) => new[] { "VID_18D1&PID_2D00", "VID_18D1&PID_2D01", "VID_18D1&PID_2D04", "VID_18D1&PID_2D05" }
        .Any(value => hardwareId.Contains(value, StringComparison.OrdinalIgnoreCase));
    public static bool IsAccessoryDataInterface(string hardwareId, string compatibleIds) => IsAccessory(hardwareId) &&
        (!hardwareId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) || hardwareId.Contains("&MI_00", StringComparison.OrdinalIgnoreCase)) &&
        !compatibleIds.Contains("Class_FF&SubClass_42&Prot_01", StringComparison.OrdinalIgnoreCase) &&
        !compatibleIds.Split(';').Any(value => value.Equals("USB\\COMPOSITE", StringComparison.OrdinalIgnoreCase));
    public static bool IsAndroidCandidate(string hardwareId, string compatibleIds) => hardwareId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
        (IsAccessoryDataInterface(hardwareId, compatibleIds) || compatibleIds.Contains("Class_FF&SubClass_42&Prot_01", StringComparison.OrdinalIgnoreCase));
}
