using System.Text.Json;
namespace BlueLink.Storage;

internal sealed record PeerPreference(string Note = "", bool Pinned = false);
// Local display metadata is deliberately not moved when two different identities are associated.
internal sealed class PeerPreferences(string directory)
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory,"peer-preferences.json");
    private Dictionary<string,PeerPreference> Read() => File.Exists(_path)
        ? new(JsonSerializer.Deserialize<Dictionary<string,PeerPreference>>(File.ReadAllText(_path)) ?? throw new IOException("Invalid peer preferences"),StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);
    internal PeerPreference Get(string peer) { lock(_gate) {
        try { return Read().GetValueOrDefault(peer) ?? new(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    } }
    internal void Update(string peer, string? note = null, bool? pinned = null)
    {
        if (string.IsNullOrWhiteSpace(peer)) throw new ArgumentException(nameof(peer));
        lock(_gate)
        {
            var rows=Read(); var old=rows.GetValueOrDefault(peer) ?? new();
            var text=note?.Trim() ?? old.Note;
            if (text.Length>64 || text.Any(char.IsControl)) throw new ArgumentException("备注最多 64 个字符，不能包含换行。");
            rows[peer]=new(text,pinned ?? old.Pinned);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary=_path+".new";
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(rows)); output.Flush(true); }
            File.Move(temporary,_path,true);
        }
    }
}
