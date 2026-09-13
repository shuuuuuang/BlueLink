namespace BlueLink.Storage;

public sealed partial class BlueLinkDatabase
{
    private static void RelocatePortablePaths(NativeSqliteConnection connection, string currentRoot)
    {
        string? previous = null;
        using (var query = connection.Prepare("SELECT value FROM app_setting WHERE key='portable_program_directory'"))
            if (query.Read()) previous = query.GetString(0);
        currentRoot = Path.GetFullPath(currentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        InTransaction(connection, () =>
        {
            if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, currentRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Names are a closed allowlist; paths are bound values, including %, _ and quotes.
                foreach (var (table, column, restriction) in new[] {
                    ("attachment", "local_path", ""), ("attachment", "preview_path", ""),
                    ("transfer", "local_path", ""), ("transfer", "snapshot_path", ""),
                    ("app_setting", "value", " AND key='download_directory'") })
                {
                    using var statement = connection.Prepare($"UPDATE {table} SET {column}=? || substr({column}, ?) WHERE substr({column}, 1, ?)=? COLLATE NOCASE{restriction}");
                    statement.Bind(1, currentRoot).Bind(2, previous.Length + 1).Bind(3, previous.Length).Bind(4, previous).ExecuteNonQuery();
                }
            }
            UpsertSetting(connection, "portable_program_directory", currentRoot);
        });
    }
}
