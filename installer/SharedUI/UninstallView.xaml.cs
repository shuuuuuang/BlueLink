namespace BlueLink.Installation
{
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using Wpf.Ui.Controls;

    public enum UninstallStage { Options, Progress, Complete, Failure, RestartRequired }
    public partial class UninstallView : UserControl
    {
        public UninstallStage Stage { get; private set; }
        public bool DeleteUserData { get => DeleteDataCheck.IsChecked == true; set => DeleteDataCheck.IsChecked = value; }
        public bool DataDeleted { get; private set; }
        public event Action RemoveRequested;
        public event Action CloseRequested;
        public event Action OpenFolderRequested;
        public event Action OpenLogRequested;
        public UninstallView() { InitializeComponent(); ShowOptions(); SizeChanged += (_, __) => BrandHeroLogo.Width = BrandHeroLogo.Height = ActualHeight < 600 ? 330 : 390; }
        public void ShowOptions() { Apply(UninstallStage.Options); Heading.Text = "卸载蓝联"; DescriptionText.Text = "卸载程序前，可以选择是否同时删除本机用户数据。"; DeleteChanged(null, null); }
        public void ShowProgress(string path, string status, int? percentage = null)
        {
            Apply(UninstallStage.Progress); Heading.Text = "正在卸载蓝联"; DescriptionText.Text = "请稍候，安装向导正在从此电脑移除蓝联。";
            InstallPath.Text = path; ProgressStatus.Text = status; Progress.IsIndeterminate = !percentage.HasValue;
            Progress.Value = Math.Max(0, Math.Min(100, percentage ?? 0)); Percent.Text = percentage.HasValue ? Progress.Value + "%" : "";
        }
        public void ShowComplete(bool deleted)
        {
            DataDeleted = deleted; Apply(UninstallStage.Complete); Heading.Text = "蓝联已成功卸载";
            DescriptionText.Text = deleted ? "蓝联程序与所选用户数据已从此电脑移除。" : "蓝联程序已从此电脑移除，你可以随时重新安装。";
            DataOutcome.Text = deleted ? "用户数据已删除" : "保留的用户数据";
            DataSummary.Text = deleted ? "用户数据已删除" : "用户数据已保留";
            DataDetail.Text = deleted ? "聊天记录、信任信息、偏好设置与可清理的临时文件已删除" : "聊天记录、信任信息与偏好设置";
            Secondary.Content = deleted ? "打开 Download" : "打开数据文件夹";
        }
        public void ShowFailure(string error, string logPath, bool restart = false)
        {
            Apply(restart ? UninstallStage.RestartRequired : UninstallStage.Failure);
            Heading.Text = restart ? "重启后完成卸载" : "卸载未完成";
            DescriptionText.Text = restart ? "Windows 仍需在重启后清理被占用的程序文件。" : "蓝联未能完成全部卸载步骤。请根据下方信息处理后重试。";
            ErrorText.Text = error; LogPathText.Text = logPath ?? ""; LogButton.Visibility = String.IsNullOrEmpty(logPath) ? Visibility.Collapsed : Visibility.Visible;
            RecoveryText.Text = restart
                ? "请保存其他工作后重启电脑，再确认卸载结果。Download 中的文件会保留。"
                : "关闭正在使用蓝联文件的程序，检查目录权限后重试。Download 中的文件会保留；用户数据清理失败原因请查看上方详情。";
        }
        private void Apply(UninstallStage stage)
        {
            Stage = stage;
            OptionsPanel.Visibility = stage == UninstallStage.Options ? Visibility.Visible : Visibility.Collapsed;
            ProgressPanel.Visibility = stage == UninstallStage.Progress ? Visibility.Visible : Visibility.Collapsed;
            CompletePanel.Visibility = stage == UninstallStage.Complete ? Visibility.Visible : Visibility.Collapsed;
            FailurePanel.Visibility = stage == UninstallStage.Failure || stage == UninstallStage.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            BrandColumn.Width = new GridLength(stage == UninstallStage.Complete ? 352 : 0);
            BrandPanel.Visibility = stage == UninstallStage.Complete ? Visibility.Visible : Visibility.Collapsed;
            var failed = stage == UninstallStage.Failure || stage == UninstallStage.RestartRequired;
            StateIconTile.Visibility = stage == UninstallStage.Complete || failed ? Visibility.Visible : Visibility.Collapsed;
            StateIconTile.Width = StateIconTile.Height = failed ? 64 : 76;
            StateIconTile.Background = failed ? new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)) : Brushes.Transparent;
            StateIcon.Width = StateIcon.Height = failed ? 64 : 76;
            StateIcon.Source = TryFindResource(failed ? "InstallerWarning" : "InstallerSuccess") as ImageSource;
            BodyScroll.Padding = new Thickness(54, stage == UninstallStage.Options ? 72 : stage == UninstallStage.Progress ? 56 : 46, 54, 24);
            Primary.Visibility = stage == UninstallStage.Progress || stage == UninstallStage.RestartRequired ? Visibility.Collapsed : Visibility.Visible;
            Primary.Appearance = stage == UninstallStage.Options ? ControlAppearance.Danger : ControlAppearance.Primary;
            Primary.Foreground = Brushes.White;
            Primary.Background = new SolidColorBrush(stage == UninstallStage.Options ? Color.FromRgb(0xF0, 0x44, 0x44) : Color.FromRgb(0x12, 0x64, 0xF6));
            Primary.BorderBrush = Primary.Background;
            Primary.Content = stage == UninstallStage.Complete ? "完成" : stage == UninstallStage.Failure ? "重试卸载" : "卸载";
            Secondary.Content = stage == UninstallStage.Options || stage == UninstallStage.Progress ? "取消" : "关闭";
            Secondary.IsEnabled = stage != UninstallStage.Progress;
            DeleteDataCheck.IsEnabled = stage == UninstallStage.Options;
            BodyScroll.ScrollToTop();
        }
        private void DeleteChanged(object sender, RoutedEventArgs args)
        {
            if (Primary == null || Stage != UninstallStage.Options) return;
            Primary.Content = DeleteUserData ? "卸载并删除" : "卸载";
            DeleteDescription.Text = DeleteUserData ? "删除聊天记录、已信任设备和蓝联设置。此操作无法撤销。" : "删除聊天记录、已信任设备和蓝联设置。";
            DeleteDataCard.Background = new SolidColorBrush(DeleteUserData ? Color.FromRgb(0xF5, 0xF8, 0xFF) : Colors.White);
            DeleteDataCard.BorderBrush = new SolidColorBrush(DeleteUserData ? Color.FromRgb(0x12, 0x64, 0xF6) : Color.FromRgb(0xD8, 0xE1, 0xEF));
            DeleteDescription.Foreground = new SolidColorBrush(DeleteUserData ? Color.FromRgb(0xB8, 0x42, 0x42) : Color.FromRgb(0x58, 0x69, 0x8D));
        }
        private void CopyRemoved_Click(object sender, RoutedEventArgs args) => Copy("程序文件、快捷方式和注册信息");
        private void CopyData_Click(object sender, RoutedEventArgs args) => Copy(DataOutcome.Text + "：" + DataDetail.Text);
        private static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }
        private void Primary_Click(object sender, RoutedEventArgs args) { if (Stage == UninstallStage.Complete) CloseRequested?.Invoke(); else if (Stage == UninstallStage.Failure) ShowOptions(); else if (Stage == UninstallStage.Options) RemoveRequested?.Invoke(); }
        private void Secondary_Click(object sender, RoutedEventArgs args) { if (Stage == UninstallStage.Complete) OpenFolderRequested?.Invoke(); else if (Stage != UninstallStage.Progress) CloseRequested?.Invoke(); }
        private void Log_Click(object sender, RoutedEventArgs args) => OpenLogRequested?.Invoke();
    }
}
