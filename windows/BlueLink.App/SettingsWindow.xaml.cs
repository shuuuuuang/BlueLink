using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using BlueLink.Domain;
using BlueLink.Storage;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class SettingsWindow : FluentWindow
{
    private readonly MainViewModel _model;
    private bool _isInitializing = true;
    private bool _isSaving;

    public SettingsWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        CloseBehaviorBox.ItemsSource = CloseBehaviorChoices;
        CloseBehaviorBox.DisplayMemberPath = nameof(CloseBehaviorChoice.Name);
        CloseBehaviorBox.SelectedValuePath = nameof(CloseBehaviorChoice.KeepBackgroundSessions);

        MaxConnectionsBox.ItemsSource = Enumerable.Range(1, 8);

        RetentionBox.ItemsSource = RetentionChoices;
        RetentionBox.DisplayMemberPath = nameof(RetentionChoice.Name);
        RetentionBox.SelectedValuePath = nameof(RetentionChoice.Value);

        PopulateRuntimeInformation();
        ApplySettingsDraft(model.Settings);
        SetPage("general");
        _isInitializing = false;
        UpdateReceiveLimitState();
        UpdateStorageSpace();
    }

    private void PopulateRuntimeInformation()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        ProductVersionText.Text = $"版本 {DisplayVersion(version)}";
        RuntimeVersionText.Text = $"Microsoft.WindowsDesktop.App {Environment.Version.Major}.{Environment.Version.Minor}.{Environment.Version.Build}";
        RuntimeArchitectureText.Text = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "ARM64",
            Architecture.Arm => "ARM",
            _ => RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private static string DisplayVersion(Version? version)
    {
        if (version is null) return "未知";
        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private void ApplySettingsDraft(BlueLinkSettings value)
    {
        ScanStartupToggle.IsChecked = value.ScanOnStartup;
        CloseBehaviorBox.SelectedValue = value.KeepBackgroundSessions;
        AutoConnectToggle.IsChecked = value.AutoConnectTrustedDevices;
        MaxConnectionsBox.SelectedItem = value.MaxConcurrentConnections;
        AutoDownloadToggle.IsChecked = value.AutoDownloadFiles;
        ReceiveLimitToggle.IsChecked = value.ReceiveSizeLimitEnabled;
        ReceiveLimitText.Text = Math.Max(1, value.ReceiveSizeLimitBytes / (1024 * 1024)).ToString();
        ThumbnailsToggle.IsChecked = value.ShowImageThumbnails;
        DownloadPathText.Text = string.IsNullOrWhiteSpace(value.DownloadDirectory)
            ? _model.DefaultDownloadDirectory
            : value.DownloadDirectory;
        SaveChatToggle.IsChecked = value.SaveChatHistory;
        SaveTransfersToggle.IsChecked = value.SaveTransferHistory;
        DiagnosticsToggle.IsChecked = value.DiagnosticsEnabled;
        RetentionBox.SelectedValue = value.RetentionPeriod;
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;
        SetPage(item.TargetPageTag?.ToString() ?? "general");
    }

    private void SetPage(string page)
    {
        GeneralPage.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPage.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = page == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;

        GeneralNavigationItem.IsActive = page == "general";
        ConnectionNavigationItem.IsActive = page == "connection";
        FilesNavigationItem.IsActive = page == "files";
        PrivacyNavigationItem.IsActive = page == "privacy";
        AboutNavigationItem.IsActive = page == "about";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;

        if (!TryReadReceiveLimit(out var receiveLimitBytes)) return;
        if (!TryPrepareDownloadDirectory(out var downloadDirectory)) return;

        var previous = _model.Settings;
        var draft = previous with
        {
            AutoConnectTrustedDevices = AutoConnectToggle.IsChecked == true,
            ScanOnStartup = ScanStartupToggle.IsChecked == true,
            KeepBackgroundSessions = CloseBehaviorBox.SelectedValue is bool keepBackground && keepBackground,
            MaxConcurrentConnections = MaxConnectionsBox.SelectedItem is int count ? count : 4,
            AutoDownloadFiles = AutoDownloadToggle.IsChecked == true,
            ReceiveSizeLimitEnabled = ReceiveLimitToggle.IsChecked == true,
            ReceiveSizeLimitBytes = receiveLimitBytes,
            ShowImageThumbnails = ThumbnailsToggle.IsChecked == true,
            DownloadDirectory = downloadDirectory,
            SaveChatHistory = SaveChatToggle.IsChecked == true,
            SaveTransferHistory = SaveTransfersToggle.IsChecked == true,
            DiagnosticsEnabled = DiagnosticsToggle.IsChecked == true,
            RetentionPeriod = RetentionBox.SelectedValue as string ?? "forever",
        };

        _isSaving = true;
        SetFooterEnabled(false);
        SetStatus(InfoBarSeverity.Informational, "正在保存", "正在验证并保存蓝联设置…");

        try
        {
            await _model.SaveSettingsAsync(draft);
            SetStatus(InfoBarSeverity.Success, "设置已保存", "新的设置已经生效。");
            await Task.Delay(450);
            _isSaving = false;
            DialogResult = true;
        }
        catch (Exception failure)
        {
            _isSaving = false;
            SetFooterEnabled(true);
            SetStatus(InfoBarSeverity.Error, "保存失败", failure.Message);
        }
    }

    private bool TryReadReceiveLimit(out long bytes)
    {
        const long multiplier = 1024L * 1024L;
        var enabled = ReceiveLimitToggle.IsChecked == true;
        if (!long.TryParse(ReceiveLimitText.Text.Trim(), out var mebibytes) || mebibytes < 1 || mebibytes > long.MaxValue / multiplier)
        {
            if (!enabled)
            {
                bytes = 500L * multiplier;
                return true;
            }

            bytes = 0;
            SetPage("files");
            ReceiveLimitText.Focus();
            SetStatus(InfoBarSeverity.Warning, "文件大小上限无效", "请输入大于 0 的 MiB 整数。未保存任何设置。");
            return false;
        }

        bytes = mebibytes * multiplier;
        return true;
    }

    private bool TryPrepareDownloadDirectory(out string path)
    {
        path = DownloadPathText.Text.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("保存目录不能为空。");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".bluelink-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception failure)
        {
            SetPage("files");
            DownloadPathText.Focus();
            SetStatus(InfoBarSeverity.Warning, "保存目录不可用", $"{failure.Message} 未保存任何设置。");
            return false;
        }
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsDraft(BlueLinkSettings.Defaults(_model.DefaultDownloadDirectory));
        UpdateReceiveLimitState();
        UpdateStorageSpace();
        SetStatus(InfoBarSeverity.Informational, "已恢复默认值草稿", "点击“保存设置”后才会写入；聊天、信任关系和接收文件均未更改。");
    }

    private void SetFooterEnabled(bool enabled)
    {
        RestoreDefaultsButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private void ReceiveLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ReceiveLimitText is null) return;
        UpdateReceiveLimitState();
    }

    private void UpdateReceiveLimitState() => ReceiveLimitText.IsEnabled = ReceiveLimitToggle.IsChecked == true;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus(InfoBarSeverity.Informational, "正在扫描", "正在查找附近运行蓝联的设备…");
            await _model.ScanAsync();
            var failed = _model.ScanFeedback.StartsWith("扫描失败", StringComparison.Ordinal);
            SetStatus(failed ? InfoBarSeverity.Error : InfoBarSeverity.Success,
                failed ? "扫描失败" : "扫描完成", _model.ScanFeedback);
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "扫描失败", failure.Message);
        }
    }

    private void BrowseDownload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "选择蓝联文件保存目录",
            InitialDirectory = DownloadPathText.Text,
        };
        if (picker.ShowDialog(this) == true) DownloadPathText.Text = picker.FolderName;
    }

    private void DownloadPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateStorageSpace();
    }

    private void UpdateStorageSpace()
    {
        if (StorageSpaceText is null || StorageUsageText is null || DownloadPathText is null) return;
        try
        {
            var candidate = DownloadPathText.Text.Trim();
            var path = string.IsNullOrWhiteSpace(candidate) ? _model.DefaultDownloadDirectory : Path.GetFullPath(candidate);
            var root = RequireNonEmpty(Path.GetPathRoot(path));
            var drive = new DriveInfo(root);
            StorageSpaceText.Text = $"可用空间：{FormatBytes(drive.AvailableFreeSpace)}";
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Data");
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Cache");
            StorageUsageText.Text = $"聊天与传输数据库 {FormatBytes(DirectoryBytes(dataRoot))}  ·  " +
                                    $"缩略图缓存 {FormatBytes(DirectoryBytes(cacheRoot))}  ·  " +
                                    $"接收文件 {FormatBytes(DirectoryBytes(path))}";
        }
        catch
        {
            StorageSpaceText.Text = "可用空间：路径尚不可用";
            StorageUsageText.Text = "存储统计将在目录可用后显示";
        }
    }

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetFullPath(DownloadPathText.Text.Trim());
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            SetStatus(InfoBarSeverity.Success, "已打开文件夹", path);
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "无法打开文件夹", failure.Message);
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Cache");
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
            Directory.CreateDirectory(cacheRoot);
            UpdateStorageSpace();
            SetStatus(InfoBarSeverity.Success, "缩略图缓存已清理", "聊天记录、接收文件和原始图片均未删除。");
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "缓存清理失败", failure.Message);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "导出蓝联脱敏诊断日志",
            FileName = $"BlueLink-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
        };
        if (picker.ShowDialog(this) != true) return;

        try
        {
            SetStatus(InfoBarSeverity.Informational, "正在导出", "正在生成脱敏诊断日志…");
            await _model.ExportDiagnosticsAsync(picker.FileName);
            SetStatus(InfoBarSeverity.Success, "诊断日志已导出", picker.FileName);
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "导出失败", failure.Message);
        }
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!BlueLinkDialog.Confirm(this, "信任管理", $"移除对 {item.PeerName} 的信任？再次连接时需要重新核对安全码。")) return;

        try
        {
            await _model.ForgetPeerAsync(item.PeerId);
            SetStatus(InfoBarSeverity.Success, "信任已移除", $"{item.PeerName} 再次连接时需要重新核对安全码。");
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "移除失败", failure.Message);
        }
    }

    private async void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmClear("聊天记录")) return;
        try
        {
            await _model.ClearChatHistoryAsync();
            SetStatus(InfoBarSeverity.Success, "聊天记录已清除", "Download 中的接收文件未被删除。");
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "清除失败", failure.Message);
        }
    }

    private async void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmClear("传输记录")) return;
        try
        {
            await _model.ClearTransferHistoryAsync();
            SetStatus(InfoBarSeverity.Success, "传输记录已清除", "Download 中的接收文件未被删除。");
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, "清除失败", failure.Message);
        }
    }

    private bool ConfirmClear(string value) => BlueLinkDialog.Confirm(this, "清除数据",
        $"确定清除本机的{value}？此操作不可撤销，但不会删除 Download 中的接收文件。");

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        SaveInfoBar.Severity = severity;
        SaveInfoBar.Title = title;
        SaveInfoBar.Message = message;
        SaveInfoBar.IsOpen = true;
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string RequireNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new IOException("路径无效") : value;

    private static string FormatBytes(long value) => value >= 1L << 30
        ? $"{value / (double)(1L << 30):0.0} GiB"
        : $"{value / (double)(1L << 20):0.0} MiB";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSaving) DialogResult = false;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isSaving) return;
        e.Handled = true;
        DialogResult = false;
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isSaving) e.Cancel = true;
    }

    private sealed record CloseBehaviorChoice(string Name, bool KeepBackgroundSessions);
    private static readonly CloseBehaviorChoice[] CloseBehaviorChoices =
    [
        new("最小化到系统托盘", true),
        new("退出蓝联", false),
    ];

    private sealed record RetentionChoice(string Name, string Value);
    private static readonly RetentionChoice[] RetentionChoices =
    [
        new("永久", "forever"),
        new("30 天", "30d"),
        new("90 天", "90d"),
        new("1 年", "1y"),
    ];
}
