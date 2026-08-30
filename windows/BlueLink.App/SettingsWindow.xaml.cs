using System.Windows;
using System.Diagnostics;
using BlueLink.Domain;
using BlueLink.Storage;
using Microsoft.Win32;

namespace BlueLink;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _model;

    public SettingsWindow(MainViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        var productVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        ProductVersionText.Text = $"蓝联 BlueLink {productVersion?.Major}.{productVersion?.Minor}.{productVersion?.Build}";
        var value = model.Settings;
        AutoConnectBox.IsChecked = value.AutoConnectTrustedDevices;
        ScanStartupBox.IsChecked = value.ScanOnStartup;
        BackgroundSessionsBox.IsChecked = value.KeepBackgroundSessions;
        MaxConnectionsBox.ItemsSource = Enumerable.Range(1, 8); MaxConnectionsBox.SelectedItem = value.MaxConcurrentConnections;
        AutoDownloadBox.IsChecked = value.AutoDownloadFiles;
        ReceiveLimitBox.IsChecked = value.ReceiveSizeLimitEnabled;
        ReceiveLimitText.Text = Math.Max(1, value.ReceiveSizeLimitBytes / (1024 * 1024)).ToString();
        ThumbnailsBox.IsChecked = value.ShowImageThumbnails;
        DownloadPathText.Text = value.DownloadDirectory;
        SaveChatBox.IsChecked = value.SaveChatHistory;
        SaveTransfersBox.IsChecked = value.SaveTransferHistory;
        DiagnosticsBox.IsChecked = value.DiagnosticsEnabled;
        RetentionBox.ItemsSource = RetentionChoices;
        RetentionBox.DisplayMemberPath = nameof(RetentionChoice.Name);
        RetentionBox.SelectedValuePath = nameof(RetentionChoice.Value);
        RetentionBox.SelectedValue = value.RetentionPeriod;
        UpdateStorageSpace();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(ReceiveLimitText.Text, out var mebibytes) || mebibytes < 1)
        { BlueLinkDialog.Show(this, "设置", "请输入有效的文件大小上限。", BlueLinkDialogTone.Warning); return; }
        var path = DownloadPathText.Text.Trim();
        try { Directory.CreateDirectory(path); var probe = Path.Combine(path, $".bluelink-{Guid.NewGuid():N}.tmp"); File.WriteAllBytes(probe, []); File.Delete(probe); }
        catch (Exception failure) { BlueLinkDialog.Show(this, "设置", $"保存目录不可写：{failure.Message}", BlueLinkDialogTone.Warning); return; }
        var previous = _model.Settings;
        await _model.SaveSettingsAsync(previous with
        {
            AutoConnectTrustedDevices = AutoConnectBox.IsChecked == true,
            ScanOnStartup = ScanStartupBox.IsChecked == true,
            KeepBackgroundSessions = BackgroundSessionsBox.IsChecked == true,
            MaxConcurrentConnections = MaxConnectionsBox.SelectedItem is int count ? count : 4,
            AutoDownloadFiles = AutoDownloadBox.IsChecked == true,
            ReceiveSizeLimitEnabled = ReceiveLimitBox.IsChecked == true,
            ReceiveSizeLimitBytes = checked(mebibytes * 1024 * 1024),
            ShowImageThumbnails = ThumbnailsBox.IsChecked == true,
            DownloadDirectory = path,
            SaveChatHistory = SaveChatBox.IsChecked == true,
            SaveTransferHistory = SaveTransfersBox.IsChecked == true,
            DiagnosticsEnabled = DiagnosticsBox.IsChecked == true,
            RetentionPeriod = RetentionBox.SelectedValue as string ?? "forever",
        });
        DialogResult = true;
    }

    private void BrowseDownload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择蓝联文件保存目录", InitialDirectory = DownloadPathText.Text };
        if (picker.ShowDialog(this) == true) DownloadPathText.Text = picker.FolderName;
    }

    private void DefaultDownload_Click(object sender, RoutedEventArgs e) =>
        DownloadPathText.Text = _model.DefaultDownloadDirectory;

    private void DownloadPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateStorageSpace();

    private void UpdateStorageSpace()
    {
        if (StorageSpaceText is null || DownloadPathText is null) return;
        try
        {
            var path = DownloadPathText.Text.Trim();
            var root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? _model.DefaultDownloadDirectory : path));
            var drive = new DriveInfo(requireNonEmpty(root));
            StorageSpaceText.Text = $"可用空间：{FormatBytes(drive.AvailableFreeSpace)}";
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Data");
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Cache");
            StorageUsageText.Text = $"聊天与传输数据库 {FormatBytes(DirectoryBytes(dataRoot))}  ·  " +
                $"缩略图缓存 {FormatBytes(DirectoryBytes(cacheRoot))}  ·  接收文件 {FormatBytes(DirectoryBytes(path))}";
        }
        catch
        {
            StorageSpaceText.Text = "可用空间：路径尚不可用";
            if (StorageUsageText is not null) StorageUsageText.Text = "存储统计将在目录可用后显示";
        }
    }

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        var path = DownloadPathText.Text.Trim();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Cache");
        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        Directory.CreateDirectory(cacheRoot);
        UpdateStorageSpace();
        BlueLinkDialog.Show(this, "文件与存储", "缩略图缓存已清理。聊天记录和原始文件未被删除。");
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch { return 0; }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "导出蓝联脱敏诊断日志", FileName = $"BlueLink-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt" };
        if (picker.ShowDialog(this) != true) return;
        await _model.ExportDiagnosticsAsync(picker.FileName);
        BlueLinkDialog.Show(this, "设置", "诊断日志已脱敏并导出。");
    }

    private static string requireNonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? throw new IOException("路径无效") : value;
    private static string FormatBytes(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GiB" : $"{value / (double)(1L << 20):0.0} MiB";
    private sealed record RetentionChoice(string Name, string Value);
    private static readonly RetentionChoice[] RetentionChoices =
    [new("永久", "forever"), new("30 天", "30d"), new("90 天", "90d"), new("1 年", "1y")];

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item } &&
            BlueLinkDialog.Confirm(this, "信任管理", $"移除对 {item.PeerName} 的信任？"))
            await _model.ForgetPeerAsync(item.PeerId);
    }

    private async void ClearChat_Click(object sender, RoutedEventArgs e)
    { if (ConfirmClear("聊天记录")) await _model.ClearChatHistoryAsync(); }
    private async void ClearTransfers_Click(object sender, RoutedEventArgs e)
    { if (ConfirmClear("传输记录")) await _model.ClearTransferHistoryAsync(); }
    private bool ConfirmClear(string value) => BlueLinkDialog.Confirm(this, "清除数据",
        $"确定清除本机的{value}？此操作不可撤销。");
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
