using System.Diagnostics;
using System.Text.Json;
using BlueLink.Storage;

namespace BlueLink.Appearance;

internal sealed record WindowSizePreference(double Width, double Height, bool Maximized = false)
{
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) &&
        Width is > 0 and <= 32768 && Height is > 0 and <= 32768;
}

internal sealed record WindowSizePolicy(double DefaultWidth, double DefaultHeight, double MinWidth, double MinHeight)
{
    public static WindowSizePolicy For(string key) => key switch
    {
        "main" => new(1180, 720, 1000, 600),
        "search" => new(860, 640, 620, 480),
        "preview" => new(1000, 700, 720, 480),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    public WindowSizePreference Resolve(WindowSizePreference? saved, double availableWidth, double availableHeight)
    {
        if (saved?.IsValid != true) saved = null;
        var width = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : DefaultWidth + 40;
        var height = double.IsFinite(availableHeight) && availableHeight > 0 ? availableHeight : DefaultHeight + 40;
        // Keep the content minimum even on displays whose logical work area is smaller.
        // A display/DPI combination below that minimum requires separate desktop acceptance.
        var maxWidth = Math.Max(MinWidth, width - 24);
        var maxHeight = Math.Max(MinHeight, height - 24);
        return new(Math.Clamp(saved?.Width ?? Math.Min(DefaultWidth, width * .9), MinWidth, maxWidth),
            Math.Clamp(saved?.Height ?? Math.Min(DefaultHeight, height * .9), MinHeight, maxHeight), saved?.Maximized == true);
    }

    public static WindowSizePreference Capture(WindowSizePreference previous, int state,
        double width, double height, double restoreWidth, double restoreHeight)
    {
        // WPF: Normal=0, Minimized=1, Maximized=2. Minimize must not replace normal bounds.
        if (state == 1) return previous;
        var normal = new WindowSizePreference(state == 2 ? restoreWidth : width,
            state == 2 ? restoreHeight : height, state == 2);
        return normal.IsValid ? normal : previous with { Maximized = state == 2 };
    }
}

/// <summary>Independent app_setting keys cannot be overwritten by a stale settings-page draft.</summary>
internal sealed class WindowPreferencesStore
{
    private readonly string _databasePath;
    private readonly string _key;
    private readonly object _gate = new();
    private long _revision;
    private WindowSizePreference? _lastSaved;

    public WindowPreferencesStore(string dataDirectory, string windowKey)
    {
        _ = WindowSizePolicy.For(windowKey);
        _databasePath = Path.Combine(dataDirectory, "bluelink.db");
        _key = "window_size." + windowKey;
    }

    public WindowSizePreference? Read()
    {
        try
        {
            if (!File.Exists(_databasePath)) return null;
            using var db = new NativeSqliteConnection(_databasePath);
            db.Execute("PRAGMA busy_timeout=250;");
            using var table = db.Prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='app_setting'");
            if (!table.Read()) return null;
            using var query = db.Prepare("SELECT value FROM app_setting WHERE key=?");
            query.Bind(1, _key);
            if (!query.Read()) return null;
            var json = query.GetString(0);
            if (json.Length > 512) return null;
            var value = JsonSerializer.Deserialize<WindowSizePreference>(json);
            return value?.IsValid == true ? value : null;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Trace.TraceWarning("Window size read failed: {0}", failure.GetType().Name); return null; }
    }

    public Task<bool> SaveAsync(WindowSizePreference value)
    {
        var revision = Interlocked.Increment(ref _revision);
        return Task.Run(() => Write(value, revision));
    }

    public bool Save(WindowSizePreference value) => Write(value, Interlocked.Increment(ref _revision));

    private bool Write(WindowSizePreference value, long revision)
    {
        if (!value.IsValid) return false;
        lock (_gate)
        {
            if (revision != Interlocked.Read(ref _revision) || value == _lastSaved) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                using var db = new NativeSqliteConnection(_databasePath);
                db.Execute("PRAGMA busy_timeout=250; CREATE TABLE IF NOT EXISTS app_setting(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
                using var query = db.Prepare("INSERT INTO app_setting(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value");
                query.Bind(1, _key).Bind(2, JsonSerializer.Serialize(value)).ExecuteNonQuery();
                _lastSaved = value;
                return true;
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
            { Trace.TraceWarning("Window size save failed: {0}", failure.GetType().Name); return false; }
        }
    }
}
