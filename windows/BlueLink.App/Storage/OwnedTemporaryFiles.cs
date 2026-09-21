using System.Text.Json;

namespace BlueLink.Storage;

public sealed record TemporaryCleanup(long Bytes, int Files, int Retained, int Errors);

// Only files created after ownership registration are eligible. Unknown historical residue is untouched.
public static class OwnedTemporaryFiles
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Owner(int Version, string Name, Guid TaskId, DateTimeOffset Created);
    public static void Register(string path, Guid taskId)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Temporary file needs an owner.",nameof(taskId));
        lock (Gate)
        {
            path = Path.GetFullPath(path); Safe(path);
            if (File.Exists(path) || Active.Contains(path)) throw new IOException("Temporary file already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var marker = path + ".owner.json"; Safe(marker);
            var row = new Owner(1, Path.GetFileName(path), taskId, DateTimeOffset.UtcNow);
            var temporary = marker + ".new"; Safe(temporary);
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(row)); output.Flush(true); }
            File.Move(temporary, marker, true);
            Active.Add(path);
        }
    }
    public static void Release(string path)
    {
        lock (Gate)
        {
            path = Path.GetFullPath(path); Active.Remove(path);
            try { Safe(path); if (!File.Exists(path)) File.Delete(path + ".owner.json"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    public static TemporaryCleanup Collect(string root, Func<Guid, bool> referenced, bool preview, TimeSpan? minimumAge = null)
    {
        lock (Gate)
        {
            root = Path.GetFullPath(root); Safe(root);
            if (!Directory.Exists(root)) return new(0,0,0,0);
            long bytes = 0; int files = 0, retained = 0, errors = 0;
            foreach (var marker in Directory.EnumerateFiles(root, "*.owner.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Safe(marker);
                    var row = JsonSerializer.Deserialize<Owner>(File.ReadAllBytes(marker));
                    if (row is null || row.Version != 1 || Path.GetFileName(row.Name) != row.Name || row.TaskId == Guid.Empty ||
                        marker != Path.Combine(root, row.Name + ".owner.json")) { errors++; continue; }
                    var path = Path.Combine(root, row.Name); Safe(path);
                    if (Active.Contains(path) || referenced(row.TaskId) || DateTimeOffset.UtcNow - row.Created < (minimumAge ?? TimeSpan.Zero))
                    { retained++; continue; }
                    if (!File.Exists(path)) { if (!preview) File.Delete(marker); continue; }
                    var length = new FileInfo(path).Length;
                    if (!preview)
                    {
                        // Recheck durable references under the same lock that protects new writers.
                        if (referenced(row.TaskId)) { retained++; continue; }
                        File.Delete(path); File.Delete(marker);
                    }
                    bytes = checked(bytes + length); files++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { errors++; }
            }
            return new(bytes,files,retained,errors);
        }
    }
    private static void Safe(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected temporary storage is not eligible for cleanup.");
    }
}
