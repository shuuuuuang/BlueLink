using System.Diagnostics;

namespace BlueLink.Storage;

public enum StorageCategory { Received, Thumbnails, Updates, Snapshots, UsbStaging, Drafts, Other }
public sealed record StorageLocation(string Path, StorageCategory Category);
public sealed record StorageInventory(IReadOnlyDictionary<StorageCategory, long> Bytes, bool Partial)
{
    public long TotalBytes => Bytes.Values.Sum();
    public static StorageInventory Scan(IEnumerable<StorageLocation> locations, CancellationToken token = default)
    {
        var roots = locations.Select(x => x with { Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(x.Path)) })
            .OrderByDescending(x => x.Path.Length).ToArray();
        var bytes = Enum.GetValues<StorageCategory>().ToDictionary(x => x, _ => 0L);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = false; var elapsed = Stopwatch.StartNew(); var count = 0;
        foreach (var root in roots)
        {
            var pending = new Stack<string>(); pending.Push(root.Path);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100000 || elapsed.Elapsed > TimeSpan.FromSeconds(5)) return new(bytes, true);
                if (!seen.Add(directory)) continue;
                try
                {
                    if (!Directory.Exists(directory)) { if (directory == root.Path) partial = true; continue; }
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++count > 100000 || elapsed.Elapsed > TimeSpan.FromSeconds(5)) return new(bytes, true);
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                        if (!seen.Add(path)) continue;
                        var category = root.Category == StorageCategory.Snapshots && System.IO.Path.GetExtension(path) == ".blm"
                            ? StorageCategory.UsbStaging : root.Category;
                        bytes[category] = checked(bytes[category] + new FileInfo(path).Length);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException) { partial = true; }
            }
        }
        return new(bytes, partial);
    }
}
