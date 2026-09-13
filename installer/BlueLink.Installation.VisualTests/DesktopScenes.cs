using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BlueLink.SetupUI;

internal static class DesktopScenes
{
    // UI-only host: no Bootstrapper/Burn engine, runtime downloader or uninstall executor.
    internal static int Run(string scene, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var allowed = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../.acceptance")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Desktop evidence must remain below the workspace .acceptance directory.");
        var scenes = new[] { "welcome", "location", "invalid-path", "low-space", "progress", "failure", "complete", "cancel", "running", "runtime-required", "runtime-failure", "runtime-downloading", "runtime-verifying", "runtime-elevation", "runtime-completed", "uninstall-keep", "uninstall-delete", "uninstall-confirm-keep", "uninstall-confirm-delete", "uninstall-progress", "uninstall-complete-keep", "uninstall-complete-delete", "uninstall-failure" };
        if (!scenes.Contains(scene)) throw new ArgumentException("Unsupported installer desktop scene.");
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new InstallerWindow();
        window.PrepareVisualAcceptance();
        window.Title = "蓝联 · QA 安装界面 · " + scene;
        window.SetDisplayVersion(File.ReadAllText(Path.Combine(allowed, "..", "VERSION")).Trim());
        window.SetInstallFolder(Path.Combine(output, "isolated-location", "BlueLink"));
        window.SetLogPath(Path.Combine(output, "qa-install.log"));
        window.ApplyCancelRequested += () => { window.ShowFailure("QA 已取消界面样本；未启动安装引擎。"); window.Close(); };
        window.RuntimeRedetectRequested += () => window.ShowRuntimeRequired("8.0.30", "55.8 MiB", "QA 尚未检测到运行时");
        window.RuntimeContinueRequested += window.ShowFreshInstall;
        if (scene == "location" || scene == "invalid-path" || scene == "low-space" || scene == "running") window.ShowOverwriteContext();
        if (scene == "invalid-path") window.ShowLocationError("QA 无法使用此位置，请选择具有写入权限的文件夹。");
        if (scene == "low-space") window.ShowLocationError("QA 可用空间不足，还需要 219 MiB。", "QA 可用空间 37 MiB");
        if (scene == "progress" || scene == "cancel") { window.ShowInstalling("QA 正在安装新版程序文件…"); window.SetProgress(56, "QA 正在安装新版程序文件…"); }
        if (scene == "failure") window.ShowFailure("QA 安装失败，错误代码：0x80070643。请查看日志后重试。");
        if (scene == "complete") window.ShowCompleted(false, window.InstallFolder);
        if (scene.StartsWith("runtime-", StringComparison.Ordinal))
        {
            window.ShowRuntimeRequired("8.0.30", "55.8 MiB", scene == "runtime-failure" ? "QA 运行环境下载或安装失败。错误代码：0x80070643。" : null);
            if (scene != "runtime-required" && scene != "runtime-failure") window.ShowRuntimeProgress(scene.Substring(8), 24000000, 58510672);
        }
        if (scene.StartsWith("uninstall-", StringComparison.Ordinal))
        {
            window.ShowUninstall();
            var view = (FrameworkElement)window.FindName("UninstallSurface");
            ((CheckBox)view.FindName("DeleteDataCheck")).IsChecked = scene.EndsWith("delete", StringComparison.Ordinal);
            if (scene == "uninstall-progress") { window.ShowInstalling("QA 正在移除程序文件…"); window.SetProgress(56, "QA 正在移除程序文件…"); }
            if (scene.StartsWith("uninstall-complete-", StringComparison.Ordinal)) window.ShowCompleted(true, window.InstallFolder);
            if (scene == "uninstall-failure") window.ShowFailure("QA 无法移除被占用的文件。错误代码：0x80070020。");
        }
        var prompted = false;
        window.ContentRendered += (_, __) =>
        {
            if (prompted) return; prompted = true;
            if (scene == "running") window.Confirm("蓝联正在运行", "QA 关闭正在运行的蓝联后继续。");
            if (scene == "cancel") window.Confirm("取消安装", "确认取消当前安装操作？");
            if (scene.StartsWith("uninstall-confirm-", StringComparison.Ordinal)) window.Confirm("确认卸载蓝联", window.DeleteUserData ? "将卸载蓝联，并删除聊天记录、已信任设备和蓝联设置。Download 中已接收的文件仍会保留。" : "将卸载蓝联程序。聊天记录、已信任设备、蓝联设置以及 Download 中已接收的文件都会保留。");
        };
        app.Run(window);
        return 0;
    }
}
