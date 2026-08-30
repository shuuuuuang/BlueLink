using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace BlueLink;

public partial class ImagePreviewWindow : Window
{
    private readonly string _path;
    private bool _fitMode = true;
    private bool _updatingZoom;
    private bool _panning;
    private Point _panStart;
    private double _horizontalStart;
    private double _verticalStart;

    public ImagePreviewWindow(string path, string title)
    {
        InitializeComponent();
        _path = path;
        Title = $"{title} · 蓝联图片预览";
        PreviewImage.Source = LoadImage(path);
        Loaded += (_, _) => FitImage();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();
    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1, fitMode: false);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(ZoomSlider.Value / 1.2, fitMode: false);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(ZoomSlider.Value * 1.2, fitMode: false);

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        ImageRotation.Angle = (ImageRotation.Angle + 90) % 360;
        if (_fitMode) FitImage();
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is null || ZoomTextButton is null) return;
        ImageScale.ScaleX = ImageScale.ScaleY = e.NewValue;
        ZoomTextButton.Content = $"{e.NewValue * 100:0}%";
        if (!_updatingZoom) _fitMode = false;
    }

    private void SetZoom(double value, bool fitMode)
    {
        _updatingZoom = true;
        ZoomSlider.Value = Math.Clamp(value, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingZoom = false;
        _fitMode = fitMode;
    }

    private void FitImage()
    {
        if (PreviewImage.Source is not BitmapSource bitmap || ImageScroller.ViewportWidth <= 0 || ImageScroller.ViewportHeight <= 0) return;
        var rotated = Math.Abs(ImageRotation.Angle % 180) > .1;
        var sourceWidth = rotated ? bitmap.Height : bitmap.Width;
        var sourceHeight = rotated ? bitmap.Width : bitmap.Height;
        var availableWidth = Math.Max(100, ImageScroller.ViewportWidth - 72);
        var availableHeight = Math.Max(100, ImageScroller.ViewportHeight - 72);
        SetZoom(Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight), fitMode: true);
        ImageScroller.ScrollToHorizontalOffset(0);
        ImageScroller.ScrollToVerticalOffset(0);
    }

    private void ImageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode && IsLoaded) Dispatcher.BeginInvoke(FitImage);
    }

    private void ImageScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        SetZoom(ZoomSlider.Value * factor, fitMode: false);
        e.Handled = true;
    }

    private void ImageStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { FitImage(); e.Handled = true; return; }
        _panning = true;
        _panStart = e.GetPosition(ImageScroller);
        _horizontalStart = ImageScroller.HorizontalOffset;
        _verticalStart = ImageScroller.VerticalOffset;
        ImageStage.CaptureMouse();
        ImageStage.Cursor = Cursors.SizeAll;
    }

    private void ImageStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ImageScroller);
        ImageScroller.ScrollToHorizontalOffset(_horizontalStart - (current.X - _panStart.X));
        ImageScroller.ScrollToVerticalOffset(_verticalStart - (current.Y - _panStart.Y));
    }

    private void ImageStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        ImageStage.ReleaseMouseCapture();
        ImageStage.Cursor = Cursors.Arrow;
    }

    private void Locate_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(
        "explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });

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
