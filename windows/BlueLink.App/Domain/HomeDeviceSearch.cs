namespace BlueLink.Domain;

public static class HomeDeviceSearch
{
    public static bool Matches(string query, params string?[] fields) =>
        string.IsNullOrWhiteSpace(query) || fields.Any(field =>
            field?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);
}
