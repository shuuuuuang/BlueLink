using System.Windows;

namespace BlueLink.Installation
{
    internal static class InstallerConfirmationDialog
    {
        internal static InstallerDialogWindow Create(Window owner, string title, string message)
        {
            var removal = title == "确认卸载蓝联";
            return new InstallerDialogWindow(title, message, owner,
                removal ? message.Contains("并删除") ? "卸载并删除" : "卸载" : "确认",
                "取消", destructive: removal);
        }
    }
}
