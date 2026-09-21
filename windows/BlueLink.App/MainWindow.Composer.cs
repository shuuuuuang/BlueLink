using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink.Domain;
using BlueLink.Files;

namespace BlueLink;

public partial class MainWindow
{
    private ComposerDrafts? _composerDrafts;
    private double _composerPreferred = 120;
    private Point? _composerResizeStart;
    private double _composerResizeHeight;
    private string? _composerPeer;
    private bool _renderingComposer;
    private bool _normalizingComposerCaret;
    private FlowDocument? _composerStyledDocument;
    // Native WPF undo serializes embedded visuals. Keep only stable string IDs in their Tag.
    private readonly Dictionary<string, ComposerAttachment> _composerAttachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComposerPreview> _composerPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _composerImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sendingComposers = new(StringComparer.OrdinalIgnoreCase);
    private void InitializeComposer()
    {
        try { _composerDrafts = _model.ComposerStore; _composerPreferred = _composerDrafts.Height; }
        catch { Loaded += (_, _) => ShowToast(Localization.Strings.Get("输入草稿未能读取，请检查存储空间"), ToastLevel.Error); }
        DataObject.AddPastingHandler(MessageInput, Composer_Pasting);
        MessageInput.SelectionChanged += (_, _) => NormalizeComposerCaret();
        CommandManager.AddPreviewExecutedHandler(MessageInput, Composer_PasteCommand);
        CommandManager.AddPreviewCanExecuteHandler(MessageInput, Composer_CanPaste);
        _model.PropertyChanged += (_, args) =>
        {
            if (!_renderingComposer && args.PropertyName is nameof(MainViewModel.DraftText) or nameof(MainViewModel.ActivePeerId))
            {
                if (_composerPeer != _model.ActivePeerId) LoadComposer();
                else if (_composerPeer is { } peer && _composerDrafts is not null && !_composerDrafts.Get(peer).Any(p => p.File is not null) &&
                    string.Concat(_composerDrafts.Get(peer).Select(p => p.Text)) != _model.DraftText)
                { _composerDrafts.Edit(peer, [new(Guid.NewGuid(), _model.DraftText)]); LoadComposer(); }
            }
        };
        _model.ComposerCleared += peer => { if (peer is null || string.Equals(peer, _composerPeer, StringComparison.OrdinalIgnoreCase)) LoadComposer(); };
        MessageInput.SizeChanged += (_, _) => RefreshComposerImageSizes();
        MessageInput.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => _composerImeActive = true), true);
        MessageInput.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, _) => _composerImeActive = true), true);
        MessageInput.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) => _composerImeActive = false), true);
        MessageInput.LostKeyboardFocus += (_, _) => _composerImeActive = false;
        ApplyComposerHeight(); LoadComposer();
    }
    private void ApplyComposerHeight()
    {
        if (MessageComposer is null) return;
        var available = (MessageComposer.Parent as FrameworkElement)?.ActualHeight ?? ActualHeight - 180;
        MessageComposer.Height = ComposerDrafts.ClampHeight(_composerPreferred, available > 0 ? available : 600);
    }
    private void ComposerResize_Down(object sender, MouseButtonEventArgs e) { _composerResizeStart = e.GetPosition(this); _composerResizeHeight = MessageComposer.ActualHeight; ((UIElement)sender).CaptureMouse(); e.Handled = true; }
    private void ComposerResize_Move(object sender, MouseEventArgs e)
    {
        if (_composerResizeStart is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        _composerPreferred = ComposerDrafts.ClampHeight(_composerResizeHeight + origin.Y - e.GetPosition(this).Y, ((FrameworkElement)MessageComposer.Parent).ActualHeight);
        ApplyComposerHeight(); e.Handled = true;
    }
    private void ComposerResize_Up(object sender, MouseButtonEventArgs e) { if (_composerResizeStart is null) return; _composerResizeStart = null; ((UIElement)sender).ReleaseMouseCapture(); SaveComposerHeight(); e.Handled = true; }
    private void ComposerResize_Key(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        _composerPreferred = ComposerDrafts.ClampHeight(MessageComposer.Height + (e.Key == Key.Up ? 16 : -16), ((FrameworkElement)MessageComposer.Parent).ActualHeight);
        ApplyComposerHeight(); SaveComposerHeight(); e.Handled = true;
    }
    private void SaveComposerHeight() { try { _composerDrafts?.SetHeight(_composerPreferred); } catch { ShowToast(Localization.Strings.Get("输入框高度未能保存"), ToastLevel.Error); } }
    private void LoadComposer()
    {
        if (MessageInput is null || _composerDrafts is null) return;
        _composerPeer = _model.ActivePeerId;
        var parts = _composerDrafts.Get(_composerPeer);
        if (_composerPeer is { } peer && !_composerDrafts.Contains(peer) && !string.IsNullOrEmpty(_model.DraftText))
        { parts = [new ComposerPart(Guid.NewGuid(), _model.DraftText)]; _composerDrafts.Edit(peer, parts); }
        _renderingComposer = true;
        try
        {
            _composerAttachments.Clear(); _composerPreviews.Clear();
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            foreach (var part in parts)
            {
                if (part.File is { } file)
                {
                    if (paragraph.Inlines.LastInline is not Run) paragraph.Inlines.Add(new Run());
                    paragraph.Inlines.Add(MakeAttachment(file));
                    paragraph.Inlines.Add(new Run());
                }
                else paragraph.Inlines.Add(new Run(part.Text ?? ""));
            }
            MessageInput.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
            ApplyComposerTypography(); MessageInput.CaretPosition = paragraph.Inlines.LastInline is Run tail ? tail.ContentEnd : MessageInput.Document.ContentEnd;
        }
        finally { _renderingComposer = false; }
        RefreshComposerPlaceholder();
    }
    internal IReadOnlyList<ComposerPart> ReadComposer()
    {
        var result = new List<ComposerPart>(); var text = new System.Text.StringBuilder();
        void Flush() { if (text.Length > 0) { result.Add(new(Guid.NewGuid(), text.ToString())); text.Clear(); } }
        void Read(Inline inline)
        {
            switch (inline)
            {
                case Run run: text.Append(run.Text); break;
                case LineBreak: text.Append('\n'); break;
                case InlineUIContainer { Child: FrameworkElement { Tag: string id } } when _composerAttachments.TryGetValue(id, out var file): Flush(); result.Add(new(file.Id, File: file)); break;
                case Span span: foreach (var child in span.Inlines) Read(child); break;
            }
        }
        var first = true;
        foreach (var block in MessageInput.Document.Blocks.OfType<Paragraph>())
        { if (!first) text.Append('\n'); first = false; foreach (var inline in block.Inlines) Read(inline); }
        Flush(); return result;
    }
    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => PersistComposer();
    private bool PersistComposer()
    {
        if (_renderingComposer || _composerPeer is not { } peer || _composerDrafts is null) return false;
        try
        {
            ApplyComposerTypography(); var parts = ReadComposer(); _composerDrafts.Edit(peer, parts);
            _renderingComposer = true;
            try { _model.DraftText = string.Concat(parts.Select(p => p.Text)); } finally { _renderingComposer = false; }
            RefreshComposerPlaceholder(); return true;
        }
        catch (Exception error) { ShowToast(error.Message, ToastLevel.Error); return false; }
    }
    private void RefreshComposerPlaceholder() { if (ComposerPlaceholder is not null) ComposerPlaceholder.Visibility = ReadComposer().Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void ApplyComposerTypography()
    {
        // Reapplying a dynamic resource during TextChanged creates an extra formatting undo unit.
        if (ReferenceEquals(_composerStyledDocument, MessageInput.Document)) return;
        _composerStyledDocument = MessageInput.Document;
        // RichTextBox.Document is not a dependency property: bind the current document
        // explicitly when drafts replace it, including WPF's coerced caret padding.
        ComposerPlaceholder.SetBinding(FrameworkElement.MarginProperty,
            new System.Windows.Data.Binding(nameof(FlowDocument.PagePadding)) { Source = MessageInput.Document });
        MessageInput.Document.FontFamily = MessageInput.FontFamily;
        MessageInput.Document.FontSize = MessageInput.FontSize;
        MessageInput.Document.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
    }
    private void RefreshComposerImageSizes()
    {
        foreach (var inline in MessageInput.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()))
        {
            if (inline.Child is Border { Child: Grid grid } && grid.Children.OfType<Image>().FirstOrDefault() is { Tag: string id } image && _composerPreviews.ContainsKey(id))
                SizeComposerThumbnail(image);
            if (inline.Child is FrameworkElement card) AlignComposerAttachment(card);
        }
    }
    private double ComposerThumbnailHeight() => Math.Clamp(MessageInput.ActualHeight > 0 ? MessageInput.ActualHeight - 14 : 40, 32, 64);
    private sealed record ComposerPreview(BitmapSource Bitmap, int Width, int Height);
    private void SizeComposerThumbnail(Image image)
    {
        var source = _composerPreviews[(string)image.Tag];
        var height = ComposerThumbnailHeight();
        var geometry = ChatThumbnailGeometry.Calculate(source.Width, source.Height, 96, height, Math.Min(48, height));
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var bounds = new Rect(0, 0, geometry.Width, geometry.Height);
            dc.DrawRectangle(Brushes.Transparent, null, bounds);
            dc.PushClip(new RectangleGeometry(bounds, 6, 6));
            dc.DrawImage(source.Bitmap, new Rect(geometry.InsetX - geometry.CropX * geometry.Scale,
                geometry.InsetY - geometry.CropY * geometry.Scale, source.Width * geometry.Scale, source.Height * geometry.Scale));
            dc.Pop();
        }
        drawing.Freeze(); image.Source = new DrawingImage(drawing);
        image.Width = geometry.Width; image.Height = geometry.Height;
    }
    private InlineUIContainer MakeAttachment(ComposerAttachment file, TextPointer? position = null)
    {
        var key = file.Id.ToString("N"); _composerAttachments[key] = file;
        var card = new Border { Tag = key, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(2) };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.ToolTip = file.Name + "\n" + FileDragDropService.FormatBytes(file.Size);
        var image = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 0, 7, 0), Stretch = Stretch.Uniform };
        image.SetResourceReference(Image.SourceProperty, "FileTypeIcon-" + FileTypeCatalog.Classify(file.Name, ""));
        var thumbnail = false;
        if (FileTypeCatalog.Classify(file.Name, "") == "file-image")
        {
            try
            {
                BitmapSource bitmap; int width, height;
                if (WebpBitmapDecoder.IsWebp(file.Path))
                {
                    var decoded = WebpBitmapDecoder.Load(file.Path, 512);
                    bitmap = decoded.Bitmap; width = decoded.Width; height = decoded.Height;
                }
                else
                {
                    using var input = File.OpenRead(file.Path);
                    var frame = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    width = frame.PixelWidth; height = frame.PixelHeight;
                    var wpf = new BitmapImage(); wpf.BeginInit(); wpf.CacheOption = BitmapCacheOption.OnLoad;
                    if (width >= height) wpf.DecodePixelWidth = Math.Min(512, width); else wpf.DecodePixelHeight = Math.Min(512, height);
                    wpf.UriSource = new Uri(file.Path); wpf.EndInit(); wpf.Freeze(); bitmap = wpf;
                }
                _composerPreviews[key] = new ComposerPreview(bitmap, width, height); image.Tag = key; SizeComposerThumbnail(image); image.Margin = new Thickness(0); thumbnail = true;
            }
            catch { /* A missing source keeps a readable file card. */ }
        }
        var grid = new Grid(); card.Child = grid;
        if (thumbnail)
        {
            card.Padding = new Thickness(0); card.BorderThickness = new Thickness(0); grid.Children.Add(image);
        }
        else
        {
            var labelWidth = new FormattedText(file.Name, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(MessageInput.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Brushes.Black, VisualTreeHelper.GetDpi(MessageInput).PixelsPerDip).WidthIncludingTrailingWhitespace;
            card.Width = Math.Clamp(labelWidth + 48, 152, 220); card.MaxWidth = Math.Max(140, Math.Min(220, MessageInput.ActualWidth > 0 ? MessageInput.ActualWidth - 8 : 220));
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(image);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); grid.Children.Add(label);
            label.Children.Add(new TextBlock { Text = file.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var size = new TextBlock { Text = FileDragDropService.FormatBytes(file.Size), FontSize = 10.5 }; size.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); label.Children.Add(size);
        }
        var container = position is null ? new InlineUIContainer(card) : new InlineUIContainer(card, position);
        container.BaselineAlignment = BaselineAlignment.Baseline;
        AlignComposerAttachment(card);
        card.SizeChanged += (_, _) => AlignComposerAttachment(card);
        return container;
    }
    private void AlignComposerAttachment(FrameworkElement card)
    {
        // WPF normally puts a UI object's baseline at its bottom. Anchor the
        // surrounding text's own baseline around the visual's vertical center.
        var metrics = new FormattedText("Ag", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(MessageInput.FontFamily, MessageInput.FontStyle,
                MessageInput.FontWeight, MessageInput.FontStretch), MessageInput.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(MessageInput).PixelsPerDip);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        TextBlock.SetBaselineOffset(card, card.DesiredSize.Height / 2 + metrics.Baseline - metrics.Height / 2);
    }
    private void NormalizeComposerCaret()
    {
        if (_renderingComposer || _normalizingComposerCaret || !MessageInput.Selection.IsEmpty || MessageInput.CaretPosition.Parent is Run) return;
        var caret = MessageInput.CaretPosition;
        foreach (var run in MessageInput.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()))
        {
            if (run.Text.Length != 0 || (run.PreviousInline is not InlineUIContainer && run.NextInline is not InlineUIContainer)) continue;
            if (caret.CompareTo(run.ElementStart) < 0 || caret.CompareTo(run.ElementEnd) > 0) continue;
            _normalizingComposerCaret = true;
            try { MessageInput.CaretPosition = run.ContentStart; }
            finally { _normalizingComposerCaret = false; }
            break;
        }
    }
    private bool IsComposerDrop(DragEventArgs e) { var point = e.GetPosition(MessageComposer); return point.X >= 0 && point.Y >= 0 && point.X <= MessageComposer.ActualWidth && point.Y <= MessageComposer.ActualHeight; }
    private void StageComposerFiles(IEnumerable<string> paths)
    {
        if (!_model.CanEditDraft || _composerPeer is null || _composerDrafts is null) return;
        var files = FileDragDropService.NormalizeFilePaths(paths);
        if (ReadComposer().Count(p => p.File is not null) + files.Count > ComposerDrafts.MaximumPendingFiles) { ShowToast(Localization.Strings.Get("待发送附件最多100个"), ToastLevel.Warning); return; }
        foreach (var path in files) InsertComposerAttachment(new(Guid.NewGuid(), path, Path.GetFileName(path), new FileInfo(path).Length));
    }
    internal void InsertComposerAttachment(ComposerAttachment file)
    {
        MessageInput.BeginChange();
        try
        {
            MessageInput.Selection.Text = "";
            var position = MessageInput.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            var before = new Run(string.Empty, position);
            var card = MakeAttachment(file, before.ElementEnd);
            // A real, empty text run gives WPF a text caret rectangle. At the UI
            // object boundary WPF otherwise shrinks its rectangle from the bottom.
            // There are no invisible characters to send or consume with Backspace.
            var after = new Run(string.Empty, card.ElementEnd);
            MessageInput.CaretPosition = after.ContentStart;
        }
        finally { MessageInput.EndChange(); }
        MessageInput.Focus(); Composer_TextChanged(MessageInput, null!);
    }
    internal void InsertComposerText(string text)
    {
        MessageInput.BeginChange();
        try
        {
            MessageInput.Selection.Text = text;
            var end = MessageInput.Selection.End;
            MessageInput.Selection.Select(end, end);
        }
        finally { MessageInput.EndChange(); }
        Composer_TextChanged(MessageInput, null!);
    }
    private static bool HasAttachmentData(IDataObject data) => data.GetDataPresent(MessageClipboard.OrderedFormat) || data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap);
    private void Composer_CanPaste(object sender, CanExecuteRoutedEventArgs e) { if (e.Command != ApplicationCommands.Paste) return; try { if (_model.CanEditDraft && Clipboard.GetDataObject() is { } data && HasAttachmentData(data)) { e.CanExecute = true; e.Handled = true; } } catch { } }
    private async void Composer_PasteCommand(object sender, ExecutedRoutedEventArgs e)
    { if (e.Command != ApplicationCommands.Paste) return; try { if (Clipboard.GetDataObject() is { } data && HasAttachmentData(data)) { e.Handled = true; await StageComposerDataAsync(data); } } catch { ShowToast(Localization.Strings.Get("无法读取剪贴板，请重试"), ToastLevel.Error); } }
    private async void Composer_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (HasAttachmentData(e.DataObject)) { e.CancelCommand(); await StageComposerDataAsync(e.DataObject); }
        else if (e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); InsertComposerText(e.DataObject.GetData(DataFormats.UnicodeText)?.ToString() ?? ""); }
        else e.CancelCommand();
    }
    internal async Task StageComposerDataAsync(IDataObject data)
    {
        if (!_model.CanEditDraft || _composerPeer is not { } peer || _composerDrafts is null) return;
        ComposerAttachment? imageItem = null;
        try
        {
            if (MessageClipboard.TryReadOrdered(data, out var ordered))
            {
                if (ReadComposer().Count(p => p.File is not null) + ordered.Count(p => p.Path is not null) > ComposerDrafts.MaximumPendingFiles)
                    throw new IOException(Localization.Strings.Get("待发送附件最多100个"));
                MessageInput.BeginChange();
                try
                {
                    var previousText = false;
                    foreach (var part in ordered)
                    {
                        if (part.Path is { } filePath) InsertComposerAttachment(new(Guid.NewGuid(), filePath, Path.GetFileName(filePath), new FileInfo(filePath).Length));
                        else InsertComposerText((previousText ? Environment.NewLine : "") + part.Text);
                        previousText = part.Text is not null;
                    }
                }
                finally { MessageInput.EndChange(); }
                return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = FileDragDropService.ExtractFilePaths(data);
                if (ReadComposer().Count(p => p.File is not null) + paths.Count > ComposerDrafts.MaximumPendingFiles)
                    throw new IOException(Localization.Strings.Get("待发送附件最多100个"));
                if (data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text)) InsertComposerText(text);
                StageComposerFiles(paths);
                return;
            }
            if (data.GetData(DataFormats.Bitmap) is not BitmapSource image)
            {
                if (data.GetData(DataFormats.UnicodeText) is string fallbackText) InsertComposerText(fallbackText);
                return;
            }
            if ((long)image.PixelWidth * image.PixelHeight > 50_000_000) throw new IOException(Localization.Strings.Get("剪贴板图片过大，请保存为文件后添加"));
            if (_composerDrafts.Get(peer).Count(p => p.File is not null) >= ComposerDrafts.MaximumPendingFiles) throw new IOException(Localization.Strings.Get("待发送附件最多100个"));
            var snapshot = image.Clone(); snapshot.Freeze();
            var id = Guid.NewGuid(); var path = _composerDrafts.NewImagePath(id);
            imageItem = new(id, path, $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png", 0, true);
            _composerImports[peer] = _composerImports.GetValueOrDefault(peer) + 1;
            InsertComposerAttachment(imageItem);
            try
            {
                await Task.Run(() => {
                    Storage.OwnedTemporaryFiles.Register(path,id);
                    var temporary = path + ".tmp";
                    try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot)); using (var output = File.Create(temporary)) encoder.Save(output); File.Move(temporary, path, true); }
                    finally { try { if (File.Exists(temporary)) File.Delete(temporary); } finally { Storage.OwnedTemporaryFiles.Release(path); } }
                });
                imageItem = imageItem with { Size = new FileInfo(path).Length };
                if (_composerDrafts.Get(peer).Any(part => part.File?.Id == id))
                {
                    _composerDrafts.Edit(peer, _composerDrafts.Get(peer).Select(part => part.File?.Id == id ? part with { File = imageItem } : part));
                    if (_composerPeer == peer)
                    {
                        var inline = MessageInput.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).FirstOrDefault(p => (p.Child as FrameworkElement)?.Tag is string key && key == id.ToString("N"));
                        if (inline is not null)
                        {
                            _renderingComposer = true;
                            try { var replacement = MakeAttachment(imageItem, inline.ElementStart); new TextRange(inline.ElementStart, inline.ElementEnd).Text = ""; }
                            finally { _renderingComposer = false; }
                            Composer_TextChanged(MessageInput, null!);
                        }
                    }
                    imageItem = null;
                }
                else { _composerDrafts.DeleteUnsubmittedImage(imageItem); imageItem = null; }
            }
            finally { _composerImports[peer]--; }
        }
        catch (Exception error)
        {
            if (imageItem is not null)
            {
                _composerDrafts.Edit(peer, _composerDrafts.Get(peer).Where(part => part.File?.Id != imageItem.Id));
                _composerDrafts.DeleteUnsubmittedImage(imageItem); if (_composerPeer == peer) LoadComposer();
            }
            ShowToast(error.Message, ToastLevel.Error);
        }
    }
}
