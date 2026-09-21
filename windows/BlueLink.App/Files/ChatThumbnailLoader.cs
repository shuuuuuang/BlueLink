using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink.Domain;

namespace BlueLink.Files;

internal static class ChatThumbnailLoader
{
    public static BitmapSource? Load(ChatAttachment attachment)
    {
        if (!attachment.IsImage) return null;
        // Never use a partial original. Once complete, measure the original rather than a reduced preview.
        var sources = new[] { attachment.CanOpen ? attachment.LocalPath : null, attachment.PreviewPath };
        foreach (var path in sources.Distinct())
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var bitmap = DisplayThumbnailCache.Load(FileInteractionService.ThumbnailDirectory, path, 480, () => Decode(path),
                    "chat-min48-max240x144-v1");
                // PNG stores DPI as integer pixels per metre, which otherwise changes the cached layout size.
                if (bitmap.DpiX == 192 && bitmap.DpiY == 192) return bitmap;
                var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
                var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
                var normalized = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 192, 192, bitmap.Format, bitmap.Palette, pixels, stride);
                normalized.Freeze(); return normalized;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException) { }
        }
        return null;
    }

    private static BitmapSource Decode(string path)
    {
        using var input = File.OpenRead(path);
        var webp = WebpBitmapDecoder.IsWebp(path) ? WebpBitmapDecoder.Load(path, 4096) : null;
        BitmapSource frame = webp?.Bitmap ?? BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var geometry = ChatThumbnailGeometry.Calculate(webp?.Width ?? frame.PixelWidth, webp?.Height ?? frame.PixelHeight);
        var pixelScaleX = frame.PixelWidth / (double)(webp?.Width ?? frame.PixelWidth);
        var pixelScaleY = frame.PixelHeight / (double)(webp?.Height ?? frame.PixelHeight);
        var left = (int)Math.Floor(geometry.CropX * pixelScaleX);
        var top = (int)Math.Floor(geometry.CropY * pixelScaleY);
        var right = Math.Min(frame.PixelWidth, (int)Math.Ceiling((geometry.CropX + geometry.CropWidth) * pixelScaleX));
        var bottom = Math.Min(frame.PixelHeight, (int)Math.Ceiling((geometry.CropY + geometry.CropHeight) * pixelScaleY));
        var crop = new CroppedBitmap(frame, new Int32Rect(left, top, right - left, bottom - top));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(geometry.InsetX, geometry.InsetY, geometry.ImageWidth, geometry.ImageHeight)));
            dc.DrawImage(crop, new Rect(geometry.InsetX + (left / pixelScaleX - geometry.CropX) * geometry.Scale,
                geometry.InsetY + (top / pixelScaleY - geometry.CropY) * geometry.Scale,
                (right - left) / pixelScaleX * geometry.Scale, (bottom - top) / pixelScaleY * geometry.Scale));
            dc.Pop();
        }
        // Preserve layout size in DIPs while retaining enough bitmap detail for high-density displays.
        var result = new RenderTargetBitmap((int)Math.Ceiling(geometry.Width * 2), (int)Math.Ceiling(geometry.Height * 2),
            192, 192, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
}
