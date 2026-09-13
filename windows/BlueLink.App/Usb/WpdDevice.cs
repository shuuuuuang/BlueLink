using System.Runtime.InteropServices;

namespace BlueLink.Usb;

/// <summary>Windows inbox WPD. All instances and streams stay on the owning MTA worker.</summary>
internal sealed class WpdDevice : IDisposable
{
    private readonly Com _device, _content, _properties, _resources;
    private static readonly PropertyKey Parent = Key(3), Name = Key(4), Format = Key(6), Type = Key(7), Size = Key(11), FileName = Key(12);
    private static readonly PropertyKey DefaultResource = new(new("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);
    private const string ObjectKeys = "EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C";
    public string Id { get; }
    public WpdDevice(string id)
    {
        Id = id;
        _device = Com.Create("F7C0039A-4762-488A-B4B3-760EF9A1BA9B", "625E2DF8-6392-4CF0-9AD1-3CFA5F17775C");
        try
        {
            using var values = Values();
            Check(_device.Method<Open>(3)(_device.Pointer, id, values.Pointer));
            _content = _device.Get(5);
            _properties = _content.Get(4);
            _resources = _content.Get(5);
        }
        catch { _resources?.Dispose(); _properties?.Dispose(); _content?.Dispose(); _device.Dispose(); throw; }
    }
    public static string[] Devices()
    {
        using var manager = Com.Create("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3", "A1567595-4C2F-4574-A6FA-ECEF917B9A40");
        Check(manager.Method<NoArgs>(4)(manager.Pointer));
        uint count = 0;
        Check(manager.Method<DeviceList>(3)(manager.Pointer, IntPtr.Zero, ref count));
        if (count > 256) throw new IOException("Too many WPD devices");
        var array = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        try
        {
            Check(manager.Method<DeviceList>(3)(manager.Pointer, array, ref count));
            var result = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                var id = TakeString(pointer);
                if (id.Contains("usb", StringComparison.OrdinalIgnoreCase)) result.Add(id);
            }
            return result.ToArray();
        }
        finally { Marshal.FreeHGlobal(array); }
    }
    public List<string> Children(string parent)
    {
        Check(_content.Method<Enumerate>(3)(_content.Pointer, 0, parent, IntPtr.Zero, out var pointer));
        using var enumerator = new Com(pointer);
        var array = Marshal.AllocHGlobal(32 * IntPtr.Size);
        try
        {
            var result = new List<string>();
            while (true)
            {
                Check(enumerator.Method<Next>(3)(enumerator.Pointer, 32, array, out var count));
                for (var i = 0; i < count; i++) result.Add(TakeString(Marshal.ReadIntPtr(array, i * IntPtr.Size)));
                if (result.Count > 100_000) throw new IOException("WPD directory is too large");
                if (count < 32) return result;
            }
        }
        finally { Marshal.FreeHGlobal(array); }
    }
    public (string Name, string Parent, long Size) Properties(string id)
    {
        using var keys = Com.Create("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F", "DADA2357-E0AD-492E-98DB-DD61C53BA353");
        foreach (var key in new[] { Name, Parent, Size }) Check(keys.Method<AddKey>(5)(keys.Pointer, key));
        Check(_properties.Method<GetValues>(5)(_properties.Pointer, id, keys.Pointer, out var pointer));
        using var values = new Com(pointer);
        Check(values.Method<GetString>(8)(values.Pointer, Name, out var name));
        var text = TakeString(name);
        Check(values.Method<GetString>(8)(values.Pointer, Parent, out var parent));
        var parentText = TakeString(parent);
        var hr = values.Method<GetSize>(14)(values.Pointer, Size, out var size);
        return (text, parentText, hr < 0 ? -1 : checked((long)size));
    }
    public string? Find(string parent, string name)
    {
        var found = Children(parent).Where(id => Properties(id).Name == name).Take(2).ToArray();
        if (found.Length > 1) throw new IOException("Ambiguous WPD path");
        return found.SingleOrDefault();
    }
    public byte[] ReadSmall(string id, int limit)
    {
        using var stream = ReadStream(id);
        using var output = new MemoryStream();
        Copy(stream, output, limit, null, () => { });
        return output.ToArray();
    }
    public void Download(string folder, string name, string target, long expectedSize, Action<long> progress, Action checkpoint)
    {
        ValidateBlobName(name);
        var id = BeforeStream(() => Find(folder, name)) ?? throw new WpdBusyBeforeTransferException(new FileNotFoundException("USB 中转文件尚未可见"));
        var props = BeforeStream(() => Properties(id));
        if (props.Parent != folder || props.Size != expectedSize) throw new IOException("USB 中转文件大小不匹配");
        using var stream = BeforeStream(() => ReadStream(id));
        // Target is private app storage; never overwrite an unrelated file.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024);
        var total = Copy(stream, output, expectedSize, progress, checkpoint);
        if (total != expectedSize) throw new IOException("USB 中转文件未完整读取");
        output.Flush(true);
    }
    public void Upload(string folder, string name, string source, Action<long> progress, Action checkpoint)
    {
        ValidateBlobName(name);
        if (BeforeStream(() => Find(folder, name)) is not null) throw new IOException("USB 中转文件已存在");
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024);
        using var values = Values();
        SetString(values, Parent, folder); SetString(values, Name, name); SetString(values, FileName, name);
        Check(values.Method<SetGuid>(27)(values.Pointer, Type, new("0085E0A6-8D34-45D7-BC5C-447E59C73D48")));
        Check(values.Method<SetGuid>(27)(values.Pointer, Format, new("30000000-AE6C-4804-98BA-C5B74696D5E7")));
        Check(values.Method<SetSize>(13)(values.Pointer, Size, (ulong)file.Length));
        var pointer = BeforeStream(() =>
        {
            Check(_content.Method<CreateData>(7)(_content.Pointer, values.Pointer, out var value, out _, IntPtr.Zero));
            return value;
        });
        using var stream = new Com(pointer);
        using var dataStream = stream.Query(new("88E04DB3-1012-4D64-9996-F703A950D3F4"));
        var committed = false;
        try
        {
            var buffer = new byte[256 * 1024];
            long total = 0;
            while (true)
            {
                checkpoint();
                var read = file.Read(buffer);
                if (read == 0) break;
                Check(stream.Method<StreamBuffer>(4)(stream.Pointer, buffer, (uint)read, out var written));
                if (written != read) throw new IOException("Short WPD write");
                total += written; progress(total);
            }
            checkpoint();
            Check(stream.Method<WithUInt>(8)(stream.Pointer, 0));
            committed = true;
        }
        finally { if (!committed) dataStream.Method<NoArgs>(15)(dataStream.Pointer); }
    }
    public void DeleteBlob(string folder, string name)
    {
        ValidateBlobName(name);
        var id = Find(folder, name);
        if (id is null) return;
        if (Properties(id).Parent != folder) throw new IOException("USB cleanup outside owned directory");
        using var ids = Com.Create("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9", "89B2E422-4F1B-4316-BCEF-A44AFEA83EB3");
        var text = Marshal.StringToCoTaskMemUni(id);
        try
        {
            var variant = new PropVariant { Type = 31, Pointer = text };
            Check(ids.Method<AddVariant>(5)(ids.Pointer, ref variant));
            Check(_content.Method<DeleteObjects>(8)(_content.Pointer, 0, ids.Pointer, out var result));
            using var results = new Com(result);
        }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    private Com ReadStream(string id)
    {
        Check(_resources.Method<GetStream>(5)(_resources.Pointer, id, DefaultResource, 0, out _, out var pointer));
        return new(pointer);
    }
    private static long Copy(Com input, Stream output, long limit, Action<long>? progress, Action checkpoint)
    {
        var buffer = new byte[256 * 1024];
        long total = 0;
        while (true)
        {
            checkpoint();
            Check(input.Method<StreamBuffer>(3)(input.Pointer, buffer, (uint)buffer.Length, out var read));
            if (read == 0) return total;
            total += read;
            if (total > limit) throw new IOException("USB read exceeds declared size");
            output.Write(buffer, 0, (int)read); progress?.Invoke(total);
        }
    }
    public static void ValidateBlobName(string name)
    {
        if (name.Length != 36 || !name.EndsWith(".blm", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..32], "N", out _)) throw new IOException("Invalid USB spool name");
    }
    public void Dispose()
    {
        _resources.Dispose(); _properties.Dispose(); _content.Dispose();
        _device.Method<NoArgs>(8)(_device.Pointer); _device.Dispose();
    }
    private static PropertyKey Key(uint id) => new(new(ObjectKeys), id);
    private static T BeforeStream<T>(Func<T> action)
    {
        try { return action(); }
        catch (COMException error) when (error.HResult == unchecked((int)0x800700AA)) { throw new WpdBusyBeforeTransferException(error); }
    }
    private static Com Values() => Com.Create("0C15D503-D017-47CE-9016-7B3F978721CC", "6848F6F2-3155-4F86-B6F5-263EEEAB3143");
    private static void SetString(Com values, PropertyKey key, string value) => Check(values.Method<SetStringValue>(7)(values.Pointer, key, value));
    private static string TakeString(IntPtr pointer) { try { return Marshal.PtrToStringUni(pointer) ?? ""; } finally { Marshal.FreeCoTaskMem(pointer); } }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [StructLayout(LayoutKind.Sequential)] private readonly struct PropertyKey(Guid format, uint id) { public readonly Guid Format = format; public readonly uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    private sealed class Com(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer { get; private set; } = pointer;
        public T Method<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Pointer), index * IntPtr.Size));
        public Com Get(int index) { Check(Method<GetInterface>(index)(Pointer, out var value)); return new(value); }
        public Com Query(Guid iid) { Check(Method<QueryInterface>(0)(Pointer, iid, out var value)); return new(value); }
        public static Com Create(string clsid, string iid)
        {
            var cls = new Guid(clsid); var face = new Guid(iid);
            Check(CoCreateInstance(ref cls, IntPtr.Zero, 1, ref face, out var value));
            return new(value);
        }
        public void Dispose() { if (Pointer == IntPtr.Zero) return; Method<NoArgs>(2)(Pointer); Pointer = IntPtr.Zero; }
    }
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WithUInt(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterface(IntPtr self, [In] in Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int Open(IntPtr self, string id, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeviceList(IntPtr self, IntPtr array, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int Enumerate(IntPtr self, uint flags, string parent, IntPtr filter, out IntPtr enumerator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Next(IntPtr self, uint count, IntPtr array, out uint fetched);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddKey(IntPtr self, [In] in PropertyKey key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetValues(IntPtr self, string id, IntPtr keys, out IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetString(IntPtr self, [In] in PropertyKey key, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(IntPtr self, [In] in PropertyKey key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int SetStringValue(IntPtr self, [In] in PropertyKey key, string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(IntPtr self, [In] in PropertyKey key, [In] in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSize(IntPtr self, [In] in PropertyKey key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateData(IntPtr self, IntPtr values, out IntPtr stream, out uint optimal, IntPtr cookie);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetStream(IntPtr self, string id, [In] in PropertyKey key, uint mode, out uint optimal, out IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StreamBuffer(IntPtr self, [In, Out] byte[] data, uint count, out uint transferred);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddVariant(IntPtr self, ref PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeleteObjects(IntPtr self, uint options, IntPtr ids, out IntPtr results);
}
