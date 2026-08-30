namespace BlueLink.SetupUI
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;

    internal static class MsiRelatedProductLocator
    {
        private const uint ErrorSuccess = 0;
        private const uint ErrorNoMoreItems = 259;

        internal static IEnumerable<string> FindInstallFolders(string upgradeCode)
        {
            Guid parsed;
            if (!Guid.TryParse(upgradeCode, out parsed)) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                var productCode = new StringBuilder(39);
                var result = MsiEnumRelatedProducts(parsed.ToString("B").ToUpperInvariant(), 0, index, productCode);
                if (result == ErrorNoMoreItems) yield break;
                if (result != ErrorSuccess) yield break;

                var value = new StringBuilder(1024);
                uint length = (uint)value.Capacity;
                if (MsiGetProductInfo(productCode.ToString(), "InstallLocation", value, ref length) != ErrorSuccess)
                    continue;
                var folder = value.ToString();
                if (!String.IsNullOrWhiteSpace(folder) && seen.Add(folder)) yield return folder;
            }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved,
            uint productIndex, StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiGetProductInfo(string productCode, string property,
            StringBuilder value, ref uint valueLength);
    }
}
