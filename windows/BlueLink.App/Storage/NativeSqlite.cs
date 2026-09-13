using System.Runtime.InteropServices;
using System.Text;

namespace BlueLink.Storage;

internal sealed class NativeSqliteConnection : IDisposable
{
    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreate = 0x00000004;
    private const int OpenFullMutex = 0x00010000;
    private IntPtr _handle;

    public NativeSqliteConnection(string path)
    {
        var result = Native.sqlite3_open_v2(path, out _handle, OpenReadWrite | OpenCreate | OpenFullMutex, null);
        if (result != Native.Ok) throw Error(result, "open database");
    }

    public NativeSqliteStatement Prepare(string sql)
    {
        EnsureOpen();
        var result = Native.sqlite3_prepare_v2(_handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != Native.Ok) throw Error(result, "prepare statement");
        return new NativeSqliteStatement(this, statement);
    }

    public void Execute(string sql)
    {
        EnsureOpen();
        var result = Native.sqlite3_exec(_handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result == Native.Ok) return;
        var message = error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        throw new InvalidDataException($"SQLite execute failed ({result}): {message ?? Message}");
    }

    internal InvalidDataException Error(int code, string operation) => new($"SQLite {operation} failed ({code}): {Message}");
    private string Message => _handle == IntPtr.Zero ? "database is closed" : Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(_handle)) ?? "unknown SQLite error";
    private void EnsureOpen() { if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeSqliteConnection)); }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.sqlite3_close_v2(_handle);
        _handle = IntPtr.Zero;
    }

    // Windows ships winsqlite3 with STDCALL; Cdecl corrupts the x86 stack.
    internal static class Native
    {
        internal const int Ok = 0;
        internal const int Row = 100;
        internal const int Done = 101;
        internal const int Null = 5;
        internal static readonly IntPtr Transient = new(-1);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string? virtualFileSystem);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_close_v2(IntPtr database);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_errmsg(IntPtr database);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr callbackArgument, out IntPtr errorMessage);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern void sqlite3_free(IntPtr value);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}

internal sealed class NativeSqliteStatement : IDisposable
{
    private readonly NativeSqliteConnection _connection;
    private IntPtr _handle;

    internal NativeSqliteStatement(NativeSqliteConnection connection, IntPtr handle) { _connection = connection; _handle = handle; }

    public NativeSqliteStatement Bind(int index, object? value)
    {
        EnsureOpen();
        var result = value switch
        {
            null or DBNull => NativeSqliteConnection.Native.sqlite3_bind_null(_handle, index),
            bool boolean => NativeSqliteConnection.Native.sqlite3_bind_int64(_handle, index, boolean ? 1 : 0),
            byte or short or int or long => NativeSqliteConnection.Native.sqlite3_bind_int64(_handle, index, Convert.ToInt64(value)),
            byte[] bytes => NativeSqliteConnection.Native.sqlite3_bind_blob(_handle, index, bytes, bytes.Length, NativeSqliteConnection.Native.Transient),
            _ => BindText(index, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
        };
        if (result != NativeSqliteConnection.Native.Ok) throw _connection.Error(result, "bind value");
        return this;
    }

    public bool Read()
    {
        EnsureOpen();
        var result = NativeSqliteConnection.Native.sqlite3_step(_handle);
        if (result == NativeSqliteConnection.Native.Row) return true;
        if (result == NativeSqliteConnection.Native.Done) return false;
        throw _connection.Error(result, "read row");
    }

    public void ExecuteNonQuery() { while (Read()) { } }
    public bool IsNull(int column) => NativeSqliteConnection.Native.sqlite3_column_type(_handle, column) == NativeSqliteConnection.Native.Null;
    public long GetInt64(int column) => NativeSqliteConnection.Native.sqlite3_column_int64(_handle, column);
    public string GetString(int column) => Marshal.PtrToStringUTF8(NativeSqliteConnection.Native.sqlite3_column_text(_handle, column)) ?? "";

    public byte[] GetBlob(int column)
    {
        var length = NativeSqliteConnection.Native.sqlite3_column_bytes(_handle, column);
        if (length <= 0) return [];
        var result = new byte[length];
        Marshal.Copy(NativeSqliteConnection.Native.sqlite3_column_blob(_handle, column), result, 0, length);
        return result;
    }

    private int BindText(int index, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return NativeSqliteConnection.Native.sqlite3_bind_text(_handle, index, bytes, bytes.Length, NativeSqliteConnection.Native.Transient);
    }

    private void EnsureOpen() { if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeSqliteStatement)); }
    public void Dispose() { if (_handle == IntPtr.Zero) return; NativeSqliteConnection.Native.sqlite3_finalize(_handle); _handle = IntPtr.Zero; }
}
