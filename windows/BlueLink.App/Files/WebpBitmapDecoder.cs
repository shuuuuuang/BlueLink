using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace BlueLink.Files;

// Decode WebP independently of optional Windows Store/WIC codecs. Other formats
// continue using WPF, including its existing orientation and color handling.
internal static class WebpBitmapDecoder
{
    internal sealed record Decoded(BitmapSource Bitmap, int Width, int Height);

    public static bool IsWebp(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        return stream.Read(header) == header.Length &&
            header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8);
    }

    public static (int Width, int Height) ReadDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid WebP image.");
        return (codec.Info.Width, codec.Info.Height);
    }

    public static Decoded Load(string path, int maximumEdge)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid WebP image.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp) throw new InvalidDataException("Expected WebP image.");
        var original = codec.Info;
        if (original.Width <= 0 || original.Height <= 0 || maximumEdge <= 0) throw new InvalidDataException("Invalid image dimensions.");
        var scale = Math.Min(1f, maximumEdge / (float)Math.Max(original.Width, original.Height));
        var size = codec.GetScaledDimensions(scale);
        // Bound native and managed allocations even when a codec cannot downsample.
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 16 * 1024 * 1024)
            throw new InvalidDataException("Image exceeds the preview decode limit.");
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixels = new SKBitmap(info);
        // Frame zero gives a stable preview for animated WebP as well.
        if (codec.GetPixels(info, pixels.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("WebP image is incomplete or corrupt.");
        var bitmap = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32,
            null, pixels.GetPixels(), checked(pixels.RowBytes * pixels.Height), pixels.RowBytes);
        bitmap.Freeze();
        return new(bitmap, original.Width, original.Height);
    }
}
