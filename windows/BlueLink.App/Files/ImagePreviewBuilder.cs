using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueLink.Files;

public static class ImagePreviewBuilder
{
    private const int MaxPreviewBytes = 300 * 1024;

    public sealed record PreviewAsset(string Path, string FileName, string MimeType);

    public static Task<PreviewAsset> CreateAsync(string sourcePath, Guid transferId, CancellationToken token) =>
        Task.Run(() => Create(sourcePath, transferId, token), token);

    private static PreviewAsset Create(string sourcePath, Guid transferId, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BlueLink", "previews");
        Directory.CreateDirectory(directory);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapSource frame = WebpBitmapDecoder.IsWebp(sourcePath)
            ? WebpBitmapDecoder.Load(sourcePath, 1280).Bitmap
            : BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var preserveAlpha = HasAlphaPixelFormat(frame.Format);
        var extension = preserveAlpha ? ".png" : ".jpg";
        var mimeType = preserveAlpha ? "image/png" : "image/jpeg";
        var fileName = $"{transferId:N}.preview{extension}";
        var target = Path.Combine(directory, fileName);
        var maxDimension = 1280;
        var quality = 84;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, maxDimension / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource image = frame;
            if (scale < 1d)
            {
                var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                transformed.Freeze();
                image = transformed;
            }
            using var bytes = new MemoryStream();
            BitmapEncoder encoder = preserveAlpha
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(bytes);
            var reachedMinimum = preserveAlpha ? maxDimension <= 320 : maxDimension <= 640 && quality <= 52;
            if (bytes.Length <= MaxPreviewBytes || reachedMinimum)
            {
                File.WriteAllBytes(target, bytes.ToArray());
                return new(target, fileName, mimeType);
            }
            if (preserveAlpha) maxDimension = Math.Max(320, (int)(maxDimension * 0.76));
            else if (quality > 56) quality -= 12;
            else { maxDimension = Math.Max(640, maxDimension - 240); quality = 76; }
        }
    }

    private static bool HasAlphaPixelFormat(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
        format == PixelFormats.Prgba64 || format == PixelFormats.Prgba128Float;
}
