using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class ImagePreviewWindow : FluentWindow
{
    private const double MinimumScale = 0.05;
    private const double MaximumScale = 8;
    private const double FitPadding = 28;
    private readonly string _path;
    private readonly BitmapSource _bitmap;
    private bool _fitMode = true;
    private bool _panning;
    private Point _panStart;
    private Point _panOffsetStart;
    private Point _offset;
    private double _scale = 1;
    private double _angle;
    private Size _imageSize;

    public ImagePreviewWindow(string path, string title)
    {
        InitializeComponent();
        _path = Path.GetFullPath(path);
        _bitmap = LoadImage(_path);
        var fileName = Path.GetFileName(_path);
        Title = $"{fileName} · 蓝联图片预览";
        TitleBarFileName.Text = fileName;
        TitleBarFileName.ToolTip = fileName;
        PreviewImage.Source = _bitmap;
        NavigatorImage.Source = _bitmap;
        Loaded += (_, _) =>
        {
            ConfigureActualPixelSize();
            FitImage();
        };
    }

    private void ConfigureActualPixelSize()
    {
        ConfigureActualPixelSize(VisualTreeHelper.GetDpi(this));
    }

    private void ConfigureActualPixelSize(DpiScale dpi)
    {
        _imageSize = ImagePreviewViewportMath.PixelSizeInDips(
            _bitmap.PixelWidth, _bitmap.PixelHeight, dpi);
        PreviewImage.Width = _imageSize.Width;
        PreviewImage.Height = _imageSize.Height;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!IsLoaded) return;
        ConfigureActualPixelSize(newDpi);
        if (_fitMode) FitImage();
        else
        {
            ClampOffset();
            ApplyViewState();
        }
    }

    private Size ViewportSize => new(ImageViewport.ActualWidth, ImageViewport.ActualHeight);

    private Size CurrentExtent =>
        ImagePreviewViewportMath.RotatedExtent(_imageSize, _angle, _scale);

    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();

    private void ActualSize_Click(object sender, RoutedEventArgs e) =>
        SetZoom(1, fitMode: false, new Point(ImageViewport.ActualWidth / 2, ImageViewport.ActualHeight / 2));

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        SetZoom(_scale / 1.2, fitMode: false, ViewportCenter());

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        SetZoom(_scale * 1.2, fitMode: false, ViewportCenter());

    private Point ViewportCenter() =>
        new(ImageViewport.ActualWidth / 2, ImageViewport.ActualHeight / 2);

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);

    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(double delta)
    {
        _angle = ImagePreviewViewportMath.NormalizeAngle(_angle + delta);
        if (_fitMode)
        {
            FitImage();
            return;
        }

        ClampOffset();
        ApplyViewState();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _angle = 0;
        _offset = new Point();
        FitImage();
    }

    private void SetZoom(double value, bool fitMode, Point? anchor = null)
    {
        var nextScale = Math.Clamp(value, MinimumScale, MaximumScale);
        if (anchor.HasValue)
        {
            _offset = ImagePreviewViewportMath.ZoomAround(
                _offset, anchor.Value, ViewportSize, _scale, nextScale);
        }

        _scale = nextScale;
        _fitMode = fitMode;
        ClampOffset();
        ApplyViewState();
    }

    private void FitImage()
    {
        if (_imageSize.Width <= 0 || _imageSize.Height <= 0 ||
            ImageViewport.ActualWidth <= 0 || ImageViewport.ActualHeight <= 0)
            return;

        _offset = new Point();
        _scale = ImagePreviewViewportMath.FitScale(
            _imageSize, _angle, ViewportSize, FitPadding, MinimumScale, MaximumScale);
        _fitMode = true;
        ApplyViewState();
    }

    private void ClampOffset()
    {
        _offset = ImagePreviewViewportMath.ClampOffset(_offset, CurrentExtent, ViewportSize);
    }

    private void ApplyViewState()
    {
        ImageTransform.Matrix = ImagePreviewViewportMath.CreateImageMatrix(
            _imageSize, _angle, _scale, ViewportSize, _offset);
        ZoomText.Text = $"{_scale * 100:0}%";
        UpdateNavigator();
    }

    private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_fitMode) FitImage();
        else
        {
            ClampOffset();
            ApplyViewState();
        }
    }

    private void ImageViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        SetZoom(_scale * factor, fitMode: false, e.GetPosition(ImageViewport));
        e.Handled = true;
    }

    private void ImageViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FitImage();
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(ImageViewport);
        _panOffsetStart = _offset;
        ImageViewport.CaptureMouse();
        ImageViewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImageViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ImageViewport);
        _offset = new Point(
            _panOffsetStart.X + current.X - _panStart.X,
            _panOffsetStart.Y + current.Y - _panStart.Y);
        _fitMode = false;
        ClampOffset();
        ApplyViewState();
    }

    private void ImageViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();

    private void ImageViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning && e.LeftButton != MouseButtonState.Pressed) EndPan();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        ImageViewport.ReleaseMouseCapture();
        ImageViewport.Cursor = Cursors.Arrow;
    }

    private void UpdateNavigator()
    {
        var navigatorWidth = NavigatorCanvas.ActualWidth > 0
            ? NavigatorCanvas.ActualWidth
            : NavigatorCanvas.Width;
        var navigatorHeight = NavigatorCanvas.ActualHeight > 0
            ? NavigatorCanvas.ActualHeight
            : NavigatorCanvas.Height;
        if (!IsLoaded || _imageSize.Width <= 0 || navigatorWidth <= 0 || navigatorHeight <= 0) return;

        var extent = CurrentExtent;
        var viewport = ViewportSize;
        if (extent.Width <= viewport.Width + 0.5 && extent.Height <= viewport.Height + 0.5)
        {
            Navigator.Visibility = Visibility.Collapsed;
            return;
        }

        var rotatedBase = ImagePreviewViewportMath.RotatedExtent(_imageSize, _angle, 1);
        var mapScale = Math.Min(
            navigatorWidth / rotatedBase.Width,
            navigatorHeight / rotatedBase.Height);
        var mapWidth = rotatedBase.Width * mapScale;
        var mapHeight = rotatedBase.Height * mapScale;
        var mapBounds = new Rect(
            (navigatorWidth - mapWidth) / 2,
            (navigatorHeight - mapHeight) / 2,
            mapWidth,
            mapHeight);

        NavigatorImage.Width = _imageSize.Width * mapScale;
        NavigatorImage.Height = _imageSize.Height * mapScale;
        NavigatorRotation.Angle = _angle;
        Canvas.SetLeft(NavigatorImage, (navigatorWidth - NavigatorImage.Width) / 2);
        Canvas.SetTop(NavigatorImage, (navigatorHeight - NavigatorImage.Height) / 2);

        var crop = ImagePreviewViewportMath.VisibleMapRect(extent, viewport, _offset, mapBounds);
        if (crop.IsEmpty)
        {
            Navigator.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(NavigatorCrop, crop.Left);
        Canvas.SetTop(NavigatorCrop, crop.Top);
        NavigatorCrop.Width = Math.Max(4, crop.Width);
        NavigatorCrop.Height = Math.Max(4, crop.Height);
        Navigator.Visibility = Visibility.Visible;
    }

    internal void LoadZoomedVisualFixture()
    {
        _fitMode = false;
        _scale = Math.Clamp(Math.Max(1.5, _scale * 2.5), MinimumScale, MaximumScale);
        _offset = new Point(-110, -70);
        ClampOffset();
        ApplyViewState();
        UpdateLayout();
    }

    private void Locate_Click(object sender, RoutedEventArgs e) => ShellFileLocator.OpenAndSelect(_path);

    private static BitmapImage LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
