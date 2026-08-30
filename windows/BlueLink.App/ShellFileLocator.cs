using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BlueLink;

internal static class ShellFileLocator
{
    public static void OpenAndSelect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var folderPath = Path.GetDirectoryName(fullPath)
            ?? throw new DirectoryNotFoundException("The file does not have a parent directory.");
        IntPtr folderPidl = IntPtr.Zero;
        IntPtr filePidl = IntPtr.Zero;
        try
        {
            ThrowIfFailed(SHParseDisplayName(folderPath, IntPtr.Zero, out folderPidl, 0, out _));
            ThrowIfFailed(SHParseDisplayName(fullPath, IntPtr.Zero, out filePidl, 0, out _));
            var childPidl = ILFindLastID(filePidl);
            if (childPidl == IntPtr.Zero) throw new Win32Exception("Windows Shell could not resolve the file.");
            ThrowIfFailed(SHOpenFolderAndSelectItems(folderPidl, 1, [childPidl], 0));
        }
        catch
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            {
                UseShellExecute = true
            });
        }
        finally
        {
            if (filePidl != IntPtr.Zero) Marshal.FreeCoTaskMem(filePidl);
            if (folderPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(folderPidl);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderItemIdList,
        uint itemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIdLists,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr itemIdList);
}
