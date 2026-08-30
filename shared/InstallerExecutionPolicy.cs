namespace BlueLink.Shared
{
    using System;

    /// <summary>
    /// Pure installer execution decisions shared by the Burn BA and the
    /// framework-only regression tests. Keep these decisions independent of
    /// WPF and WiX event objects so legacy-upgrade behaviour remains testable.
    /// </summary>
    public static class InstallerExecutionPolicy
    {
        public static bool IsEmbeddedRelatedExecution(bool embeddedDisplay, bool hasRelation) =>
            embeddedDisplay || hasRelation;

        public static bool ShouldVerifyStandaloneUninstall(bool uninstalling, bool embeddedRelatedExecution) =>
            uninstalling && !embeddedRelatedExecution;

        public static bool ShouldPlanRelatedBundleRemoval(
            bool uninstalling,
            string detectedVersion,
            string firstEmbeddedSafeVersion)
        {
            if (uninstalling) return false;

            Version detected;
            Version firstSafe;
            if (!Version.TryParse(NormalizeVersion(detectedVersion), out detected) ||
                !Version.TryParse(NormalizeVersion(firstEmbeddedSafeVersion), out firstSafe))
                return false;

            return detected >= firstSafe;
        }

        public static string GetExecutePhase(
            string packageId,
            bool runtimeOnlyPlan,
            bool uninstalling,
            bool relatedBundle)
        {
            if (runtimeOnlyPlan) return "正在安装 .NET Desktop Runtime…";
            if (uninstalling) return "正在删除程序文件和安装注册…";
            if (relatedBundle) return "正在迁移旧安装注册…";
            if (String.Equals(packageId, "BlueLinkMsi", StringComparison.OrdinalIgnoreCase))
                return "正在安装新版程序文件…";
            return "正在执行安装步骤…";
        }

        private static string NormalizeVersion(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            var separator = value.IndexOfAny(new[] { '-', '+' });
            return separator < 0 ? value.Trim() : value.Substring(0, separator).Trim();
        }
    }
}
