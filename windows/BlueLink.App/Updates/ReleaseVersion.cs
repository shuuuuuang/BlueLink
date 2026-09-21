using System.Text.RegularExpressions;

namespace BlueLink.Updates;

/// <summary>The release tag identifies preview builds independently of the numeric assembly version.</summary>
public sealed record ReleaseVersion(Version Version, int? Preview) : IComparable<ReleaseVersion>
{
    public string Tag => "v" + Version + (Preview is { } number ? "-preview." + number : "");
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var comparison = Version.CompareTo(other.Version);
        if (comparison != 0) return comparison;
        if (Preview is null) return other.Preview is null ? 0 : 1;
        return other.Preview is null ? -1 : Preview.Value.CompareTo(other.Preview.Value);
    }
    public static ReleaseVersion? Parse(string? tag)
    {
        var match = Regex.Match(tag ?? "", @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview\.([1-9][0-9]*))?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch) ||
            (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out _))) return null;
        return new(new(major, minor, patch), match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : null);
    }
}
