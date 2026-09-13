#if NET8_0_OR_GREATER
#nullable disable
#endif
namespace BlueLink.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;

    // Called only after a confirmed uninstall has satisfied its postconditions.
    // Download locations and attachment files are read before removing the database.
    internal static class UserDataCleanup
    {
        internal static int DeleteSelectedData(string localAppData, string installRoot)
        {
            var root = Path.GetFullPath(Path.Combine(localAppData, "BlueLink"));
            if (!Directory.Exists(root)) return 0;
            EnsureOrdinaryPath(root, root);
            var database = Path.Combine(root, "Data", "bluelink.db");
            var protectedPaths = new List<string> { Path.Combine(installRoot, "Download"), Path.Combine(root, "Received"), Path.Combine(root, "Download") };
            if (File.Exists(database))
            {
                EnsureOrdinaryPath(root, database);
                protectedPaths.AddRange(ReadReceivedPaths(database));
            }
            var protectedFull = protectedPaths.Where(p => !String.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToArray();
            var selected = new List<string>();
            foreach (var relative in new[] { "identity.json", "identity.json.tmp", "identity-recovery.log", "Data/bluelink.db", "Data/bluelink.db-wal", "Data/bluelink.db-shm", "Data/bluelink.db-journal" })
            {
                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!File.Exists(path)) continue;
                EnsureOrdinaryPath(root, path);
                if (IsProtected(path, protectedFull)) throw new IOException("用户接收目录与应用数据文件重叠，已停止清理。接收文件会保留。");
                selected.Add(path);
            }
            // Without the index (including a retry after it was removed), any cache
            // folder could have been selected as a receive location. Preserve it.
            if (File.Exists(database))
                foreach (var folder in new[] { "Cache", "Logs" })
                    Collect(root, Path.Combine(root, folder), protectedFull, selected, 0);
            foreach (var path in Directory.EnumerateFiles(root, "identity.*.json", SearchOption.TopDirectoryOnly))
            {
                EnsureOrdinaryPath(root, path);
                if (!IsProtected(path, protectedFull)) selected.Add(path);
            }
            selected = selected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Hold every selected file exclusively until preflight is complete. A locked file
            // must not result in a partially deleted identity or database.
            var handles = new List<FileStream>();
            try { foreach (var path in selected) handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)); }
            finally { foreach (var handle in handles) handle.Dispose(); }
            var quarantine = Path.Combine(root, ".uninstall-" + Guid.NewGuid().ToString("N"));
            var moved = new List<Tuple<string, string>>();
            try
            {
                Directory.CreateDirectory(quarantine);
                foreach (var source in selected)
                {
                    EnsureOrdinaryPath(root, source);
                    var target = Path.Combine(quarantine, moved.Count.ToString("D8"));
                    File.Move(source, target); moved.Add(Tuple.Create(source, target));
                }
            }
            catch
            {
                for (var i = moved.Count - 1; i >= 0; i--)
                    if (File.Exists(moved[i].Item2) && !File.Exists(moved[i].Item1)) File.Move(moved[i].Item2, moved[i].Item1);
                if (Directory.Exists(quarantine) && !Directory.EnumerateFileSystemEntries(quarantine).Any()) Directory.Delete(quarantine);
                throw;
            }
            foreach (var pair in moved) { EnsureOrdinaryPath(root, pair.Item2); File.Delete(pair.Item2); }
            EnsureOrdinaryPath(root, quarantine); Directory.Delete(quarantine, false);
            return selected.Count;
        }

        private static bool IsProtected(string path, IEnumerable<string> protectedPaths) => protectedPaths.Any(p =>
            path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        private static void Collect(string root, string folder, string[] protectedPaths, List<string> files, int depth)
        {
            if (!Directory.Exists(folder) || IsProtected(folder, protectedPaths)) return;
            EnsureOrdinaryPath(root, folder);
            if (depth > 32 || files.Count > 100000) throw new IOException("数据目录过大，已停止自动清理。");
            foreach (var path in Directory.EnumerateFiles(folder))
            { EnsureOrdinaryPath(root, path); if (!IsProtected(path, protectedPaths)) files.Add(path); }
            foreach (var child in Directory.EnumerateDirectories(folder)) Collect(root, child, protectedPaths, files, depth + 1);
        }
        private static void EnsureOrdinaryPath(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var boundary = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("数据清理路径超出蓝联目录。");
            for (var current = full; current.Length >= boundary.Length; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("数据目录包含链接，已停止清理以保护接收文件。");
        }
        private static IEnumerable<string> ReadReceivedPaths(string database)
        {
            IntPtr db;
            var code = sqlite3_open_v2(Utf8(database), out db, 1, IntPtr.Zero);
            if (code != 0) { if (db != IntPtr.Zero) sqlite3_close(db); throw new IOException("无法读取接收文件保护信息，用户数据未删除。"); }
            var values = new List<string>();
            try
            {
                foreach (var sql in new[] { "SELECT value FROM app_setting WHERE key='download_directory'", "SELECT local_path FROM attachment WHERE local_path IS NOT NULL", "SELECT local_path FROM transfer WHERE direction='Incoming' AND local_path IS NOT NULL" })
                {
                    IntPtr statement;
                    if (sqlite3_prepare_v2(db, Utf8(sql), -1, out statement, IntPtr.Zero) != 0) throw new IOException("接收文件索引无法读取，用户数据未删除。");
                    try
                    {
                        int step;
                        while ((step = sqlite3_step(statement)) == 100)
                        {
                            var pointer = sqlite3_column_text(statement, 0); var length = sqlite3_column_bytes(statement, 0);
                            if (length > 32768 || values.Count > 100000) throw new IOException("接收文件索引超出安全清理范围。");
                            if (pointer == IntPtr.Zero || length == 0) continue;
                            var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); values.Add(Encoding.UTF8.GetString(bytes));
                        }
                        if (step != 101) throw new IOException("接收文件索引读取中断，用户数据未删除。");
                    }
                    finally { sqlite3_finalize(statement); }
                }
            }
            finally { sqlite3_close(db); }
            return values;
        }
        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}
