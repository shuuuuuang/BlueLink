using System.Diagnostics;

namespace BlueLink.Storage;

public sealed record DirectoryUsage(long Bytes, bool Partial);

public static class StorageUsage
{
    public static DirectoryUsage Measure(string path, CancellationToken token = default)
    {
        if (!Directory.Exists(path)) return new(0, false);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return new(0, true);
        var elapsed = Stopwatch.StartNew();
        long bytes = 0;
        var count = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100000 || elapsed.Elapsed > TimeSpan.FromSeconds(5)) return new(bytes, true);
                try { bytes = checked(bytes + new FileInfo(file).Length); }
                catch (IOException) { return new(bytes, true); }
                catch (UnauthorizedAccessException) { return new(bytes, true); }
            }
            // Inaccessible entries are skipped, so this is an estimate rather than an accounting total.
            return new(bytes, false);
        }
        catch (IOException) { return new(bytes, true); }
        catch (UnauthorizedAccessException) { return new(bytes, true); }
    }
}
