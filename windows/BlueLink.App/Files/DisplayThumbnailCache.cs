using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace BlueLink.Files;

/// <summary>Regenerable UI bitmaps, separate from received preview attachments and originals.</summary>
public static class DisplayThumbnailCache
{
    private static readonly object Gate = new();
    public static BitmapSource Load(string root, string source, int width, Func<BitmapSource> decode, string profile = "")
    {
        lock (Gate)
        {
            var info = new FileInfo(source);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{width}|{profile}")));
            var target = Path.Combine(root, key + ".png");
            try
            {
                VerifyRoot(root);
                if (File.Exists(target) && !IsLink(target))
                {
                    using var input = File.OpenRead(target);
                    var cached = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    cached.Freeze(); return cached;
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException) { }
            var bitmap = decode();
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(root); VerifyRoot(root);
                var files = new DirectoryInfo(root).EnumerateFiles("*.png").Where(f => !IsLink(f.FullName)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
                var bytes = files.Sum(f => f.Length); var count = files.Length;
                foreach (var file in files)
                {
                    if (bytes < 32L * 1024 * 1024 && count < 128) break;
                    var size = file.Length; file.Delete(); bytes -= size; count--;
                }
                temporary = Path.Combine(root, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(temporary)) encoder.Save(output);
                File.Move(temporary, target, true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException) { }
            finally { if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
            return bitmap;
        }
    }
    public static void Clear(string root)
    {
        lock (Gate)
        {
            VerifyRoot(root);
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.png")) if (!IsLink(file)) File.Delete(file);
        }
    }
    private static void VerifyRoot(string root)
    {
        if (!string.Equals(Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)), "Thumbnails", StringComparison.Ordinal))
            throw new IOException("Invalid display-thumbnail cache directory");
        if (Directory.Exists(root) && IsLink(root)) throw new IOException("Redirected thumbnail cache directory");
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
