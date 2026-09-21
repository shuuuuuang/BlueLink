namespace BlueLink.Files;

/// <summary>Fit until the short edge reaches 48, then center-crop instead of shrinking further.</summary>
internal sealed record ChatThumbnailGeometry(double Width, double Height, double Scale,
    double CropX, double CropY, double CropWidth, double CropHeight)
{
    public double ImageWidth => CropWidth * Scale;
    public double ImageHeight => CropHeight * Scale;
    public double InsetX => (Width - ImageWidth) / 2;
    public double InsetY => (Height - ImageHeight) / 2;

    public static ChatThumbnailGeometry Calculate(double width, double height, double maxWidth = 240, double maxHeight = 144, double minimum = 48)
    {
        if (!double.IsFinite(minimum) || minimum <= 0 || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(maxWidth) || !double.IsFinite(maxHeight) || maxWidth < minimum || maxHeight < minimum)
            throw new ArgumentOutOfRangeException(nameof(width));
        var shortEdge = Math.Min(width, height);
        var fit = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        var scale = shortEdge <= minimum ? 1 : Math.Max(fit, minimum / shortEdge);
        var viewportWidth = Math.Clamp(width * scale, minimum, maxWidth);
        var viewportHeight = Math.Clamp(height * scale, minimum, maxHeight);
        var cropWidth = Math.Min(width, viewportWidth / scale);
        var cropHeight = Math.Min(height, viewportHeight / scale);
        return new(viewportWidth, viewportHeight, scale, (width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight);
    }
}
