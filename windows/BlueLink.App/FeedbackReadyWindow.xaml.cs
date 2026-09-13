using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using BlueLink.Feedback;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class FeedbackReadyWindow : FluentWindow
{
    private readonly FeedbackPackage _package;

    public FeedbackReadyWindow(FeedbackPackage package)
    {
        _package = package;
        InitializeComponent();
        PackageName.Text = Path.GetFileName(package.Path);
        PackageName.ToolTip = package.Path;
        PackageDirectory.Text = Path.GetDirectoryName(package.Path);
        PackageDirectory.ToolTip = package.Path;
        DiagnosticsNotice.Text = package.IncludesDiagnostics
            ? Localization.Strings.Get("已附加诊断事件摘要 · 不包含聊天内容和用户文件")
            : Localization.Strings.Get("未附加诊断信息 · 不包含聊天内容和用户文件");
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_package.Path);
            OperationStatus.Foreground = (System.Windows.Media.Brush)FindResource("SettingsSuccessBrush");
            OperationStatus.Text = Localization.Strings.Get("路径已复制");
        }
        catch (Exception failure)
        {
            OperationStatus.Foreground = (System.Windows.Media.Brush)FindResource("SettingsDangerBrush");
            OperationStatus.Text = Localization.Strings.Format($"复制失败：{failure.Message}");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_package.Path)) throw new FileNotFoundException(Localization.Strings.Get("反馈包已移动或删除。"));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_package.Path}\"") { UseShellExecute = true });
            OperationStatus.Text = "";
        }
        catch (Exception failure)
        {
            OperationStatus.Foreground = (System.Windows.Media.Brush)FindResource("SettingsDangerBrush");
            OperationStatus.Text = Localization.Strings.Format($"打开失败：{failure.Message}");
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
