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

public partial class SettingsPage : System.Windows.Controls.UserControl, IDisposable
{
    private readonly MainViewModel _model;
    private bool _isInitializing = true;
    private bool _isSaving;
    private bool _isConfirmingLeave;
    private string[] _savedDraft = [];
    private CancellationTokenSource? _storageCancellation;
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private bool _disposed;
    public event Action? LeaveRequested;
    private event EventHandler? Disposed;
    public bool CanLeave => !_isSaving && !_isGeneratingFeedback && !_isConfirmingLeave;
    private Window HostWindow => Window.GetWindow(this) ?? Application.Current.MainWindow
        ?? throw new InvalidOperationException("Settings must be hosted in the main window.");

    private void RequestLeave() { if (CanLeave) LeaveRequested?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
        LeaveRequested = null;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (SettingsScroll is null) return;
        var compact = args.NewSize.Width < 1200;
        NavigationColumn.Width = FooterNavigationColumn.Width = new(compact ? 208 : 224);
        SettingsNavigation.OpenPaneLength = SettingsNavigation.CompactPaneLength = compact ? 208 : 224;
        var pageInset = (Thickness)FindResource("SettingsPageInset");
        HelpPage.Margin = pageInset;
        FooterActions.Margin = pageInset;
    }

    public SettingsPage(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        ObserveUpdates();
        FeedbackCategory.ItemsSource = BlueLink.Feedback.FeedbackDraft.Categories.Select(value => new PreferenceChoice(value, value)).ToArray();
        FeedbackCategory.DisplayMemberPath = nameof(PreferenceChoice.Name);
        FeedbackCategory.SelectedValuePath = nameof(PreferenceChoice.Value);
        FeedbackCategory.SelectedIndex = 0;
        Disposed += (_, _) => _storageCancellation?.Cancel();

        ThemeBox.ItemsSource = new[] { new PreferenceChoice(Localization.Strings.Get("跟随系统"), "system"), new PreferenceChoice(Localization.Strings.Get("浅色"), "light"), new PreferenceChoice(Localization.Strings.Get("深色"), "dark") };
        LanguageBox.ItemsSource = new[] { new PreferenceChoice("简体中文", "zh-CN"), new PreferenceChoice("繁體中文", "zh-TW"), new PreferenceChoice("English", "en-US") };

        SendShortcutBox.ItemsSource = new[] {
            new PreferenceChoice(Localization.Strings.Get("Enter 发送"), ComposerShortcuts.Enter),
            new PreferenceChoice(Localization.Strings.Get("Ctrl+Enter 发送"), ComposerShortcuts.ControlEnter)
        };

        CloseBehaviorBox.ItemsSource = CloseBehaviorChoices;
        CloseBehaviorBox.DisplayMemberPath = nameof(CloseBehaviorChoice.Name);
        CloseBehaviorBox.SelectedValuePath = nameof(CloseBehaviorChoice.KeepBackgroundSessions);

        DuplicatePolicyBox.ItemsSource = new[] { new PreferenceChoice(Localization.Strings.Get("自动重命名"), "rename"), new PreferenceChoice(Localization.Strings.Get("每次询问"), "ask"), new PreferenceChoice(Localization.Strings.Get("覆盖同名文件"), "overwrite") };

        RetentionBox.ItemsSource = RetentionChoices;
        RetentionBox.DisplayMemberPath = nameof(RetentionChoice.Name);
        RetentionBox.SelectedValuePath = nameof(RetentionChoice.Value);

        PopulateRuntimeInformation();
        ApplySettingsDraft(model.Settings);
        _savedDraft = CaptureDraft();
        SetPage("general");
        _isInitializing = false;
        UpdateReceiveLimitState();
        UpdateStorageSpace();
    }

    private void PopulateRuntimeInformation()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        ProductVersionText.Text = Localization.Strings.Format($"版本 {Updates.UpdateService.CurrentRelease.Tag.TrimStart('v')}");
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
        if (version is null) return Localization.Strings.Get("未知");
        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private void ApplySettingsDraft(BlueLinkSettings value)
    {
        ThemeBox.SelectedValue = Appearance.AppearancePreferences.NormalizeTheme(value.Theme);
        LanguageBox.SelectedValue = Appearance.AppearancePreferences.NormalizeLanguage(value.Language);
        SendShortcutBox.SelectedValue = ComposerShortcuts.Normalize(value.SendShortcut);
        ConnectionUsbToggle.IsChecked = value.UsbEnabled;
        ScanStartupToggle.IsChecked = value.ScanOnStartup;
        CloseBehaviorBox.SelectedValue = value.KeepBackgroundSessions;
        AutoConnectToggle.IsChecked = value.AutoConnectTrustedDevices;
        AutoDownloadToggle.IsChecked = !value.AutoDownloadFiles;
        DiscoveryToggle.IsChecked = value.AllowDiscovery;
        ReconnectToggle.IsChecked = value.ReconnectAfterDisconnect;
        DuplicatePolicyBox.SelectedValue = value.DuplicateFilePolicy;
        MessageNotificationsToggle.IsChecked = value.MessageNotifications;
        ConnectionNotificationsToggle.IsChecked = value.ConnectionNotifications;
        TransferNotificationsToggle.IsChecked = value.TransferNotifications;
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

    public void ShowConnections() => SetPage("connection");
    public void ShowFiles() => SetPage("files");

    private void SetPage(string page)
    {
        var reading = page is "help" or "policy";
        var editable = page is not ("about" or "help" or "policy");
        HelpPage.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        SettingsFooterRow.Height = new GridLength(editable ? 56 : 0);
        SettingsFooter.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        HelpNavigationScroll.ScrollToTop();
        HelpDetailScroll.ScrollToTop();
        LicenseNavigationScroll.ScrollToTop();
        FooterActions.Visibility = page is "about" or "help" or "policy" ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroll.ScrollToTop();
        GeneralPage.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPage.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = page == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;

        GeneralNavigationItem.IsActive = page == "general";
        ConnectionNavigationItem.IsActive = page == "connection";
        FilesNavigationItem.IsActive = page == "files";
        PrivacyNavigationItem.IsActive = page == "privacy";
        AboutNavigationItem.IsActive = page is "about" or "help" or "policy";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveDraftAsync()) RequestLeave();
    }

    private async Task<bool> SaveDraftAsync()
    {
        if (_isSaving) return false;
        if (!TryReadReceiveLimit(out var receiveLimitBytes)) return false;
        if (!TryPrepareDownloadDirectory(out var downloadDirectory)) return false;

        var previous = _model.Settings;
        var draft = previous with
        {
            Theme = ThemeBox.SelectedValue as string ?? "system",
            Language = LanguageBox.SelectedValue as string ?? "zh-CN",
            SendShortcut = ComposerShortcuts.Normalize(SendShortcutBox.SelectedValue as string),
            UsbEnabled = ConnectionUsbToggle.IsChecked == true,
            AutoConnectTrustedDevices = AutoConnectToggle.IsChecked == true,
            ScanOnStartup = ScanStartupToggle.IsChecked == true,
            KeepBackgroundSessions = CloseBehaviorBox.SelectedValue is bool keepBackground && keepBackground,
            AutoDownloadFiles = AutoDownloadToggle.IsChecked != true,
            AllowDiscovery = DiscoveryToggle.IsChecked == true,
            ReconnectAfterDisconnect = ReconnectToggle.IsChecked == true,
            DuplicateFilePolicy = DuplicatePolicyBox.SelectedValue as string ?? "rename",
            MessageNotifications = MessageNotificationsToggle.IsChecked == true,
            ConnectionNotifications = ConnectionNotificationsToggle.IsChecked == true,
            TransferNotifications = TransferNotificationsToggle.IsChecked == true,
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
        SetStatus(InfoBarSeverity.Informational, Localization.Strings.Get("正在保存"), Localization.Strings.Get("正在验证并保存蓝联设置…"));

        try
        {
            await _model.SaveSettingsAsync(draft);
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("设置已保存"), Localization.Strings.Get("新的设置已经生效。"));
            _savedDraft = CaptureDraft();
            _isSaving = false;
            return true;
        }
        catch (Exception failure)
        {
            _isSaving = false;
            SetFooterEnabled(true);
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("保存失败"), failure.Message);
            return false;
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
            SetStatus(InfoBarSeverity.Warning, Localization.Strings.Get("文件大小上限无效"), Localization.Strings.Get("请输入大于 0 的 MiB 整数。未保存任何设置。"));
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
            if (string.IsNullOrWhiteSpace(path)) throw new IOException(Localization.Strings.Get("保存目录不能为空。"));
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
            SetStatus(InfoBarSeverity.Warning, Localization.Strings.Get("保存目录不可用"), Localization.Strings.Format($"{failure.Message} 未保存任何设置。"));
            return false;
        }
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsDraft(BlueLinkSettings.Defaults(_model.DefaultDownloadDirectory));
        UpdateReceiveLimitState();
        UpdateStorageSpace();
        SetStatus(InfoBarSeverity.Informational, Localization.Strings.Get("已恢复默认值草稿"), Localization.Strings.Get("点击“保存设置”后才会写入；聊天、信任关系和接收文件均未更改。"));
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

    private void BrowseDownload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = Localization.Strings.Get("选择蓝联文件保存目录"),
            InitialDirectory = DownloadPathText.Text,
        };
        if (picker.ShowDialog(HostWindow) == true) DownloadPathText.Text = picker.FolderName;
    }

    private void DownloadPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateStorageSpace();
    }

    private async void UpdateStorageSpace()
    {
        if (StorageSpaceText is null || StorageUsageText is null || DownloadPathText is null) return;
        _storageCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _storageCancellation = cancellation;
        var token = cancellation.Token;
        var candidate = DownloadPathText.Text.Trim();
        StorageUsageText.Text = Localization.Strings.Get("正在统计存储占用…");
        try
        {
            var path = string.IsNullOrWhiteSpace(candidate) ? _model.DefaultDownloadDirectory : Path.GetFullPath(candidate);
            await Task.Delay(300, token);
            await _storageGate.WaitAsync(token);
            try
            {
                var result = await Task.Run(() =>
                {
                    var drive = new DriveInfo(RequireNonEmpty(Path.GetPathRoot(path)));
                    return (Free: drive.AvailableFreeSpace, Inventory: _model.MeasureStorage(path, token));
                }, token);
                token.ThrowIfCancellationRequested();
                StorageSpaceText.Text = Localization.Strings.Format($"可用空间：{FormatBytes(result.Free)}");
                StorageUsageText.Text = string.Join("  ·  ", result.Inventory.Bytes.Select(pair =>
                    Localization.Strings.Get(pair.Key switch { StorageCategory.Received => "接收文件", StorageCategory.Thumbnails => "缩略图",
                        StorageCategory.Updates => "更新包", StorageCategory.Snapshots => "发送快照", StorageCategory.UsbStaging => "USB 中转",
                        StorageCategory.Drafts => "草稿附件", _ => "其他应用数据" }) + " " + FormatBytes(pair.Value))) +
                    (result.Inventory.Partial ? Localization.Strings.Get("（部分目录未统计）") : "");
            }
            finally { _storageGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                StorageSpaceText.Text = Localization.Strings.Get("可用空间：路径尚不可用");
                StorageUsageText.Text = Localization.Strings.Get("存储统计将在目录可用后显示");
            }
        }
        finally
        {
            if (ReferenceEquals(_storageCancellation, cancellation)) _storageCancellation = null;
            cancellation.Dispose();
        }
    }

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetFullPath(DownloadPathText.Text.Trim());
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("已打开文件夹"), path);
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("无法打开文件夹"), failure.Message);
        }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = await Task.Run(() => _model.CollectTemporaryFiles(preview: true));
            var thumbnails = await Task.Run(() => StorageUsage.Measure(Path.Combine(_model.CacheDirectory, "Thumbnails")));
            if (!ConfirmationWindow.Show(HostWindow, new ConfirmationDocument(Localization.Strings.Get("清理临时文件"),
                Localization.Strings.Get("预计可回收") + " " + FormatBytes(preview.Bytes + thumbnails.Bytes),
                Localization.Strings.Get("仅清理可再生成的缩略图和无引用的自有发送缓存；待恢复任务、草稿、更新包与用户文件保留。"),
                Localization.Strings.Get("清理")))) return;
            var result = await Task.Run(() => {
                BlueLink.Files.DisplayThumbnailCache.Clear(Path.Combine(_model.CacheDirectory, "Thumbnails"));
                return _model.CollectTemporaryFiles(preview: false);
            });
            UpdateStorageSpace();
            SetStatus(result.Errors == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Localization.Strings.Get(result.Errors == 0 ? "临时文件已清理" : "部分临时文件未能清理"),
                Localization.Strings.Get("聊天记录、接收文件和原始图片均未删除。"));
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("缓存清理失败"), failure.Message);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = Localization.Strings.Get("导出蓝联脱敏诊断日志"),
            FileName = $"BlueLink-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = Localization.Strings.Get("日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt"),
        };
        if (picker.ShowDialog(HostWindow) != true) return;

        try
        {
            SetStatus(InfoBarSeverity.Informational, Localization.Strings.Get("正在导出"), Localization.Strings.Get("正在生成脱敏诊断日志…"));
            await _model.ExportDiagnosticsAsync(picker.FileName);
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("诊断日志已导出"), picker.FileName);
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("导出失败"), failure.Message);
        }
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!ConfirmationWindow.Show(HostWindow, ConfirmationDocument.RemoveTrust(item.PeerName))) return;

        try
        {
            await _model.ForgetPeerAsync(item.PeerId);
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("信任已移除"), Localization.Strings.Format($"{item.PeerName} 再次连接时需要重新核对安全码。"));
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("移除失败"), failure.Message);
        }
    }

    private void EditDeviceName_Click(object sender, RoutedEventArgs e)
    {
        using var overlay = BlueLinkDialog.DimOwner(HostWindow, "#4D0D1729", 0);
        if (new DeviceNameWindow(_model) { Owner = HostWindow }.ShowDialog() == true)
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("设备名已保存"), Localization.Strings.Get("后续发现和连接会使用新的本机设备名。"));
    }

    private async void ForgetAllPeers_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.HasTrustedDevices) return;
        var dialog = new RemoveTrustedDevicesWindow(_model.TrustedDevices) { Owner = HostWindow };
        using (BlueLinkDialog.DimOwner(HostWindow, "#4D0D1729", 0))
            if (dialog.ShowDialog() != true) return;
        try
        {
            foreach (var peer in dialog.Peers) await _model.ForgetPeerAsync(peer.PeerId);
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("信任已全部移除"), Localization.Strings.Get("再次连接设备时需要重新核对安全码。"));
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("批量移除未完成"), Localization.Strings.Format($"剩余信任关系已保留，可重试。{failure.Message}"));
        }
    }

    private async void ResetIdentity_Click(object sender, RoutedEventArgs e)
    {
        using (BlueLinkDialog.DimOwner(HostWindow, "#4D0D1729", 0))
            if (new ResetIdentityWindow { Owner = HostWindow }.ShowDialog() != true) return;
        _isSaving = true;
        SetFooterEnabled(false);
        ResetIdentityButton.IsEnabled = false;
        SettingsScroll.IsEnabled = SettingsNavigation.IsEnabled = false;
        SetStatus(InfoBarSeverity.Informational, Localization.Strings.Get("正在重置设备标识"), Localization.Strings.Get("正在关闭旧连接并生成新的设备身份…"));
        try
        {
            var warning = await _model.ResetIdentityAsync();
            SetStatus(warning is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Localization.Strings.Get("设备标识已重置"), warning ?? Localization.Strings.Get("再次连接时需要核对新的安全码。聊天记录和接收文件已保留。"));
        }
        catch (Exception failure) { SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("重置失败"), failure.Message); }
        finally
        {
            _isSaving = false;
            SetFooterEnabled(true);
            ResetIdentityButton.IsEnabled = SettingsScroll.IsEnabled = SettingsNavigation.IsEnabled = true;
        }
    }

    private async void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmationWindow.Show(HostWindow, ConfirmationDocument.ClearAll(files: false))) return;
        try
        {
            await _model.ClearChatHistoryAsync();
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("聊天记录已清除"), Localization.Strings.Get("Download 中的接收文件未被删除。"));
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("清除失败"), failure.Message);
        }
    }

    private async void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmationWindow.Show(HostWindow, ConfirmationDocument.ClearAll(files: true))) return;
        try
        {
            await _model.ClearTransferHistoryAsync();
            SetStatus(InfoBarSeverity.Success, Localization.Strings.Get("传输记录已清除"), Localization.Strings.Get("Download 中的接收文件未被删除。"));
        }
        catch (Exception failure)
        {
            SetStatus(InfoBarSeverity.Error, Localization.Strings.Get("清除失败"), failure.Message);
        }
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        if (_disposed) return;
        var level = severity switch
        {
            InfoBarSeverity.Success => ToastLevel.Success,
            InfoBarSeverity.Warning => ToastLevel.Warning,
            InfoBarSeverity.Error => ToastLevel.Error,
            _ => ToastLevel.Info,
        };
        var owner = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        owner?.ShowToast(string.IsNullOrWhiteSpace(message) ? title : $"{title}\n{message}", level);
    }

    private static string RequireNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new IOException(Localization.Strings.Get("路径无效")) : value;

    private static string FormatBytes(long value) => value >= 1L << 30
        ? $"{value / (double)(1L << 30):0.0} GiB"
        : $"{value / (double)(1L << 20):0.0} MiB";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSaving && !_isGeneratingFeedback) RequestLeave();
    }

    private void SettingsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isSaving || _isGeneratingFeedback) return;
        e.Handled = true;
        RequestLeave();
    }

    private sealed record PreferenceChoice(string Label, string Value)
    {
        public string Name => Localization.Strings.Get(Label);
        public override string ToString() => Name;
    }
    private sealed record CloseBehaviorChoice(string Label, bool KeepBackgroundSessions)
    {
        public string Name => Localization.Strings.Get(Label);
    }
    private static readonly CloseBehaviorChoice[] CloseBehaviorChoices =
    [
        new("最小化到系统托盘", true),
        new("退出蓝联", false),
    ];

    private sealed record RetentionChoice(string Label, string Value)
    {
        public string Name => Localization.Strings.Get(Label);
    }
    private static readonly RetentionChoice[] RetentionChoices =
    [
        new("永久", "forever"),
        new("7 天", "7d"),
        new("30 天", "30d"),
        new("90 天", "90d"),
        new("1 年", "1y"),
    ];
}
