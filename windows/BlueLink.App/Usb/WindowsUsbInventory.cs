using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BlueLink.Usb;

/// <summary>Read-only enumeration. Never installs drivers or changes a device's configuration.</summary>
public sealed class WindowsUsbInventory
{
    private static readonly Guid UsbInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    public IReadOnlyList<UsbDevice> Enumerate(CancellationToken token)
    {
        var result = new List<UsbDevice>();
        var devices = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 2 | 4);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; index < 4096; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var hardware = Property(devices, ref info, 1);
                var compatible = Property(devices, ref info, 2);
                if (!AoaProtocol.IsAndroidCandidate(hardware, compatible)) continue;
                // Never open ADB bulk endpoints as accessory data, including the secondary AOA interface.
                if (AoaProtocol.IsAccessory(hardware) && !AoaProtocol.IsAccessoryDataInterface(hardware, compatible)) continue;
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(devices, ref info, id, id.Capacity, out _)) continue;
                var driver = Property(devices, ref info, 4);
                var name = Property(devices, ref info, 12);
                if (string.IsNullOrWhiteSpace(name)) name = Property(devices, ref info, 0);
                CM_Get_DevNode_Status(out _, out var problem, info.DevInst, 0);
                var path = "";
                foreach (var guid in InterfaceGuids(devices, ref info).Append(UsbInterface).Distinct())
                {
                    path = InterfacePath(id.ToString(), guid);
                    if (path.Length > 0) break;
                }
                result.Add(new(id.ToString(), name, path, driver, AoaProtocol.IsAccessoryDataInterface(hardware, compatible), true,
                    problem, problem is 22 or 48 or 52));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return result.GroupBy(value => value.InstanceId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
    }
    private static string Property(IntPtr set, ref DeviceInfo info, uint property)
    {
        var bytes = new byte[8192];
        return SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, bytes, (uint)bytes.Length, out _)
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0').Replace('\0', ';') : "";
    }
    private static Guid[] InterfaceGuids(IntPtr set, ref DeviceInfo info)
    {
        var handle = SetupDiOpenDevRegKey(set, ref info, 1, 0, 1, 0x20019);
        if (handle == new IntPtr(-1)) return [];
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, true));
        using var parameters = key.OpenSubKey("Device Parameters");
        // Firmware OS descriptors use the singular name; INF registrations commonly use MULTI_SZ.
        return ParseInterfaceGuids(parameters?.GetValue("DeviceInterfaceGUIDs"), parameters?.GetValue("DeviceInterfaceGUID"),
            key.GetValue("DeviceInterfaceGUIDs"), key.GetValue("DeviceInterfaceGUID"));
    }
    internal static Guid[] ParseInterfaceGuids(params object?[] registrations)
    {
        return registrations.SelectMany(value => value is string[] items ? items : value is string item ? new[] { item } : [])
            .Select(value => Guid.TryParse(value, out var guid) ? guid : Guid.Empty)
            .Where(value => value != Guid.Empty).Distinct().ToArray();
    }
    private static string InterfacePath(string instanceId, Guid guid)
    {
        // Query interfaces for this exact instance. A device-info set without
        // DIGCF_DEVICEINTERFACE does not contain enumerable interface entries.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, ref guid, instanceId, 0) != 0 || length is < 2 or > 65536)
                return "";
            var buffer = new char[length];
            var result = CM_Get_Device_Interface_List(ref guid, instanceId, buffer, length, 0);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL: interface set changed between calls.
            if (result != 0) return "";
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        return "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo info, uint scope, uint profile, uint type, uint access);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid guid, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid guid, string instanceId, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
}
