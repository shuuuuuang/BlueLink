namespace BlueLink.Storage;

public sealed partial class BlueLinkDatabase
{
    public Task<IReadOnlySet<string>> LoadReferencedFilePathsAsync(CancellationToken token = default) => Run<IReadOnlySet<string>>(() =>
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var query = connection.Prepare("SELECT local_path FROM attachment WHERE local_path IS NOT NULL UNION SELECT preview_path FROM attachment WHERE preview_path IS NOT NULL UNION SELECT local_path FROM transfer WHERE local_path IS NOT NULL UNION SELECT snapshot_path FROM transfer WHERE snapshot_path IS NOT NULL");
        while (query.Read()) paths.Add(Path.GetFullPath(query.GetString(0)));
        return paths;
    }, token);
}
