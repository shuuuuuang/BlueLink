using System.Text;

namespace BlueLink.Session;

internal static class SessionLog
{
    private static readonly object Gate = new();
    public static volatile bool Enabled = true;
    public static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Logs");
    public static string FilePath => Path.Combine(DirectoryPath, "windows-session.log");

    public static void Write(string component, string message, Exception? failure = null)
    {
        if (!Enabled) return;
        try
        {
            var directory = DirectoryPath;
            Directory.CreateDirectory(directory);
            var line = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                .Append(component).Append(" | ").Append(message);
            if (failure is not null)
                line.Append(" | ").Append(failure.GetType().Name)
                    .Append(" (0x").Append(failure.HResult.ToString("X8")).Append("): ")
                    .Append(failure.Message).AppendLine().Append(failure.StackTrace);
            line.AppendLine().AppendLine();
            lock (Gate)
                File.AppendAllText(Path.Combine(directory, "windows-session.log"), line.ToString());
        }
        catch
        {
            // Diagnostics must never replace the transport failure being recorded.
        }
    }
}
