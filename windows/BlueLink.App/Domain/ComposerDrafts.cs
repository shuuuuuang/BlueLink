using System.Text.Json;

namespace BlueLink.Domain;

internal sealed record ComposerAttachment(Guid Id, string Path, string Name, long Size, bool Owned = false);
internal sealed record ComposerPart(Guid Id, string? Text = null, ComposerAttachment? File = null);
internal sealed record ComposerState(double Height, Dictionary<string, List<ComposerPart>> Drafts,
    Dictionary<string, List<ComposerPart>> Sending);

internal sealed class ComposerDrafts
{
    public const int MaximumDropFiles = 10;
    public const int MaximumPendingFiles = 100;
    private readonly string _root, _manifest;
    private readonly object _gate = new();
    private ComposerState _state;
    private bool _editedThisProcess;
    public ComposerDrafts(string dataDirectory)
    {
        _root = System.IO.Path.Combine(dataDirectory, "composer"); _manifest = System.IO.Path.Combine(_root, "drafts.json");
        _state = File.Exists(_manifest) ? JsonSerializer.Deserialize<ComposerState>(File.ReadAllText(_manifest)) ?? throw new IOException("Invalid composer draft")
            : new(120, new(), new());
        _state = _state with { Height = ClampHeight(_state.Height, 1000), Drafts = new(_state.Drafts, StringComparer.OrdinalIgnoreCase),
            Sending = new(_state.Sending ?? new(), StringComparer.OrdinalIgnoreCase) };
        foreach (var peer in _state.Sending.Keys.ToArray()) Restore(peer);
    }
    public double Height { get { lock (_gate) return _state.Height; } }
    public static double ClampHeight(double preferred, double available) => Math.Clamp(double.IsFinite(preferred) ? preferred : 120, 120, Math.Max(120, Math.Min(360, available - 180)));
    public static bool AcceptsDrop(int count) => count is > 0 and <= MaximumDropFiles;
    public static IReadOnlyList<ComposerPart> Messages(IEnumerable<ComposerPart> parts)
    {
        var result = new List<ComposerPart>();
        foreach (var part in parts)
        {
            if (part.File is null && result.Count > 0 && result[^1].File is null) result[^1] = result[^1] with { Text = result[^1].Text + part.Text };
            else result.Add(part);
        }
        return result.Where(p => p.File is not null || !string.IsNullOrWhiteSpace(p.Text)).ToArray();
    }
    // The identity journal is already explicitly confirmed. JSON commits before the DB move;
    // empty source tombstones make replay safe if that later transaction is interrupted.
    public IReadOnlyDictionary<string, string> Associate(IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> legacy)
    {
        lock (_gate)
        {
            var map = new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in map.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                var target = old; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (map.TryGetValue(target, out var next))
                { if (!seen.Add(target)) throw new IOException("设备身份关联存在循环。"); target = next; }
                targets[old] = target;
            }
            if (targets.Count == 0) return new Dictionary<string, string>();
            var affected = targets.Keys.Concat(targets.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (affected.Any(peer => _state.Sending.ContainsKey(peer) || Get(peer).Any(p => p.File is { Size: < 0 } or { Owned: true, Size: 0 })))
                throw new IOException(Localization.Strings.Get("附件仍在准备或发送，请结束后重试身份关联。"));
            var drafts = new Dictionary<string, List<ComposerPart>>(_state.Drafts, StringComparer.OrdinalIgnoreCase);
            foreach (var peer in affected)
                if (!drafts.ContainsKey(peer) && legacy.TryGetValue(peer, out var text))
                    drafts[peer] = string.IsNullOrEmpty(text) ? [] : [new(Guid.NewGuid(), text)];
            foreach (var (old, target) in targets)
            {
                if (!drafts.TryGetValue(old, out var source)) continue;
                var destination = drafts.GetValueOrDefault(target) ?? [];
                var combined = destination.ToList();
                if (combined.Count > 0 && source.Count > 0) combined.Add(new(Guid.NewGuid(), "\n"));
                combined.AddRange(source); drafts[target] = combined; drafts[old] = [];
            }
            Save(_state with { Drafts = drafts });
            return targets.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(drafts.ContainsKey)
                .ToDictionary(peer => peer, peer => string.Concat(drafts[peer].Select(p => p.Text)), StringComparer.OrdinalIgnoreCase);
        }
    }
    public bool Contains(string peer) { lock (_gate) return _state.Drafts.ContainsKey(peer); }
    public IReadOnlyList<ComposerPart> Get(string? peer) { lock (_gate) return peer is not null && _state.Drafts.TryGetValue(peer, out var items) ? items.ToArray() : []; }
    public void Clear(string? peer = null)
    {
        lock (_gate)
        {
            var drafts = new Dictionary<string, List<ComposerPart>>(_state.Drafts, StringComparer.OrdinalIgnoreCase);
            var sending = new Dictionary<string, List<ComposerPart>>(_state.Sending, StringComparer.OrdinalIgnoreCase);
            foreach (var key in peer is null ? drafts.Keys.Concat(sending.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : new[] { peer })
            { drafts[key] = []; sending.Remove(key); }
            Save(_state with { Drafts = drafts, Sending = sending });
        }
    }
    public void SetHeight(double value) { lock (_gate) Save(_state with { Height = ClampHeight(value, 1000) }); }
    public void Edit(string peer, IEnumerable<ComposerPart> parts)
    {
        lock (_gate)
        {
            var items = parts.ToList();
            if (items.Count(p => p.File is not null) > Math.Max(MaximumPendingFiles, Get(peer).Count(p => p.File is not null))) throw new IOException(Localization.Strings.Get("待发送附件最多100个"));
            Save(_state with { Drafts = CopyWith(_state.Drafts, peer, items) });
        }
    }
    public IReadOnlyList<ComposerPart> Take(string peer)
    {
        lock (_gate)
        {
            if (_state.Sending.ContainsKey(peer)) throw new InvalidOperationException("Composer is already sending");
            var parts = Messages(Get(peer)).ToList();
            Save(_state with { Drafts = CopyWith(_state.Drafts, peer, []), Sending = CopyWith(_state.Sending, peer, parts) });
            return parts;
        }
    }
    public void Acknowledge(string peer, Guid part)
    {
        lock (_gate)
        {
            var remaining = _state.Sending.GetValueOrDefault(peer) ?? [];
            if (remaining.Count == 0 || remaining[0].Id != part) throw new InvalidOperationException("Composer order mismatch");
            Save(_state with { Sending = CopyWith(_state.Sending, peer, remaining.Skip(1).ToList()) });
        }
    }
    public void Restore(string peer)
    {
        lock (_gate)
        {
            var remaining = _state.Sending.GetValueOrDefault(peer) ?? [];
            var sending = new Dictionary<string, List<ComposerPart>>(_state.Sending, StringComparer.OrdinalIgnoreCase); sending.Remove(peer);
            Save(_state with { Drafts = CopyWith(_state.Drafts, peer, remaining.Concat(Get(peer)).ToList()), Sending = sending });
        }
    }
    internal Storage.TemporaryCleanup CollectStartupOrphans(IReadOnlySet<string> referenced, TimeSpan? minimumAge = null)
    {
        lock (_gate)
        {
            // Once editing starts, native undo can retain files no longer present in the draft.
            if (_editedThisProcess) return new(0,0,0,0);
            var files = _state.Drafts.Values.Concat(_state.Sending.Values).SelectMany(x => x).Where(x => x.File is not null)
                .Select(x => x.File!.Id).ToHashSet();
            return Storage.OwnedTemporaryFiles.Collect(System.IO.Path.Combine(_root,"images"),
                id => files.Contains(id) || referenced.Contains(NewImagePath(id)), preview:false, minimumAge:minimumAge ?? TimeSpan.FromDays(1));
        }
    }
    public string NewImagePath(Guid id) { var directory = System.IO.Path.Combine(_root, "images"); Directory.CreateDirectory(directory); return System.IO.Path.Combine(directory, id.ToString("N") + ".png"); }
    public void DeleteUnsubmittedImage(ComposerAttachment item)
    {
        if (!item.Owned || !string.Equals(System.IO.Path.GetFullPath(item.Path), NewImagePath(item.Id), StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(item.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static Dictionary<string, List<ComposerPart>> CopyWith(Dictionary<string, List<ComposerPart>> source, string peer, List<ComposerPart> parts) => new(source, StringComparer.OrdinalIgnoreCase) { [peer] = parts };
    private void Save(ComposerState next)
    {
        _editedThisProcess = true;
        Directory.CreateDirectory(_root); var temporary = _manifest + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next)); File.Move(temporary, _manifest, true); _state = next;
    }
}
