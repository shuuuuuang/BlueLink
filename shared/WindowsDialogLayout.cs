using System.Windows;

namespace BlueLink.Presentation
{
    // One geometry contract for the .NET 8 client and .NET Framework installation hosts.
    public static class WindowsDialogLayout
    {
        public const double Width = 460;
        public const double NoticeMinHeight = 220;
        public const double ConfirmationMinHeight = 250;
        public const double ActionHeight = 36;
        public const double IconTileSize = 36;
        public static GridLength HeaderRow => new GridLength(62);
        public static GridLength FooterRow => new GridLength(68);
        public static Thickness BodyMargin => new Thickness(16, 10, 16, 10);
    }
}
