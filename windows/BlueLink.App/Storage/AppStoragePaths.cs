namespace BlueLink.Storage;

/// <summary>Portable packages opt in with a marker; installed and development paths stay unchanged.</summary>
public static class AppStoragePaths
{
    public const string PortableMarker = "BlueLink.portable";
    public static string ProgramDirectory => ResolveProgramDirectory(AppContext.BaseDirectory);
    public static bool IsPortable => File.Exists(Path.Combine(ProgramDirectory, PortableMarker));
    public static string UserDirectory => IsPortable ? Path.Combine(ProgramDirectory, "Data") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink");
    public static string DatabaseDirectory => Path.Combine(UserDirectory, IsPortable ? "Database" : "Data");
    public static string ResolveProgramDirectory(string baseDirectory)
    {
        var directory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        if (File.Exists(Path.Combine(directory.FullName, PortableMarker))) return directory.FullName;
        return directory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null
            ? directory.Parent.FullName : directory.FullName;
    }
}
