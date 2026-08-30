using System;
using System.Windows;
using System.Windows.Media;

namespace BlueLink;

public static class ImagePreviewViewportMath
{
    public static Size PixelSizeInDips(int pixelWidth, int pixelHeight, DpiScale dpi)
    {
        return new Size(
            Math.Max(0, pixelWidth) / Math.Max(0.1, dpi.DpiScaleX),
            Math.Max(0, pixelHeight) / Math.Max(0.1, dpi.DpiScaleY));
    }

    public static Matrix CreateImageMatrix(Size imageSize, double angle, double scale,
        Size viewportSize, Point offset)
    {
        var radians = NormalizeAngle(angle) * Math.PI / 180d;
        var cosine = SnapToZero(Math.Cos(radians));
        var sine = SnapToZero(Math.Sin(radians));
        var m11 = scale * cosine;
        var m12 = scale * sine;
        var m21 = -scale * sine;
        var m22 = scale * cosine;
        var imageCenterX = imageSize.Width / 2;
        var imageCenterY = imageSize.Height / 2;
        var viewportCenterX = viewportSize.Width / 2 + offset.X;
        var viewportCenterY = viewportSize.Height / 2 + offset.Y;
        return new Matrix(
            m11,
            m12,
            m21,
            m22,
            viewportCenterX - imageCenterX * m11 - imageCenterY * m21,
            viewportCenterY - imageCenterX * m12 - imageCenterY * m22);
    }

    public static Rect TransformedBounds(Size imageSize, Matrix matrix)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return Rect.Empty;
        var corners = new[]
        {
            matrix.Transform(new Point(0, 0)),
            matrix.Transform(new Point(imageSize.Width, 0)),
            matrix.Transform(new Point(0, imageSize.Height)),
            matrix.Transform(new Point(imageSize.Width, imageSize.Height))
        };
        var left = Math.Min(Math.Min(corners[0].X, corners[1].X), Math.Min(corners[2].X, corners[3].X));
        var top = Math.Min(Math.Min(corners[0].Y, corners[1].Y), Math.Min(corners[2].Y, corners[3].Y));
        var right = Math.Max(Math.Max(corners[0].X, corners[1].X), Math.Max(corners[2].X, corners[3].X));
        var bottom = Math.Max(Math.Max(corners[0].Y, corners[1].Y), Math.Max(corners[2].Y, corners[3].Y));
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    public static Size RotatedExtent(Size imageSize, double angle, double scale)
    {
        var normalized = NormalizeAngle(angle);
        var swapsAxes = normalized is 90 or 270;
        return new Size(
            Math.Max(0, (swapsAxes ? imageSize.Height : imageSize.Width) * scale),
            Math.Max(0, (swapsAxes ? imageSize.Width : imageSize.Height) * scale));
    }

    public static double FitScale(Size imageSize, double angle, Size viewportSize,
        double padding, double minimum, double maximum)
    {
        var unscaled = RotatedExtent(imageSize, angle, 1);
        if (unscaled.Width <= 0 || unscaled.Height <= 0 ||
            viewportSize.Width <= 0 || viewportSize.Height <= 0)
            return minimum;

        var availableWidth = Math.Max(1, viewportSize.Width - padding * 2);
        var availableHeight = Math.Max(1, viewportSize.Height - padding * 2);
        return Math.Clamp(Math.Min(availableWidth / unscaled.Width, availableHeight / unscaled.Height),
            minimum, maximum);
    }

    public static Point ClampOffset(Point offset, Size extent, Size viewportSize)
    {
        var maximumX = Math.Max(0, (extent.Width - viewportSize.Width) / 2);
        var maximumY = Math.Max(0, (extent.Height - viewportSize.Height) / 2);
        return new Point(
            Math.Clamp(offset.X, -maximumX, maximumX),
            Math.Clamp(offset.Y, -maximumY, maximumY));
    }

    public static Point ZoomAround(Point offset, Point anchor, Size viewportSize,
        double oldScale, double newScale)
    {
        if (oldScale <= 0 || Math.Abs(oldScale - newScale) < double.Epsilon) return offset;
        var viewportCenter = new Point(viewportSize.Width / 2, viewportSize.Height / 2);
        var imageCenter = new Point(viewportCenter.X + offset.X, viewportCenter.Y + offset.Y);
        var ratio = newScale / oldScale;
        return new Point(
            offset.X + (1 - ratio) * (anchor.X - imageCenter.X),
            offset.Y + (1 - ratio) * (anchor.Y - imageCenter.Y));
    }

    public static Rect VisibleMapRect(Size extent, Size viewportSize, Point offset, Rect mapBounds)
    {
        if (extent.Width <= 0 || extent.Height <= 0 || mapBounds.IsEmpty) return Rect.Empty;

        var imageLeft = (viewportSize.Width - extent.Width) / 2 + offset.X;
        var imageTop = (viewportSize.Height - extent.Height) / 2 + offset.Y;
        var visibleLeft = Math.Max(0, -imageLeft);
        var visibleTop = Math.Max(0, -imageTop);
        var visibleRight = Math.Min(extent.Width, viewportSize.Width - imageLeft);
        var visibleBottom = Math.Min(extent.Height, viewportSize.Height - imageTop);
        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop) return Rect.Empty;

        return new Rect(
            mapBounds.Left + visibleLeft / extent.Width * mapBounds.Width,
            mapBounds.Top + visibleTop / extent.Height * mapBounds.Height,
            (visibleRight - visibleLeft) / extent.Width * mapBounds.Width,
            (visibleBottom - visibleTop) / extent.Height * mapBounds.Height);
    }

    public static int NormalizeAngle(double angle)
    {
        var normalized = (int)Math.Round(angle) % 360;
        if (normalized < 0) normalized += 360;
        return normalized;
    }

    private static double SnapToZero(double value) => Math.Abs(value) < 0.0000001 ? 0 : value;
}
