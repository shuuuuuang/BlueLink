using System.IO;
using BlueLink.Appearance;
using BlueLink.Security;
using BlueLink.Storage;

internal sealed class WindowSizeVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkWindowSize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = WindowSizePolicy.For("main");
            Check(policy.Resolve(null, 1920, 1040) == new WindowSizePreference(1180, 720), "modest initial size");
            var normal = new WindowSizePreference(1260, 780);
            Check(policy.Resolve(normal, 1920, 1040) == normal, "a saved user size overrides defaults");
            var small = policy.Resolve(new(100, 120), 1920, 1040);
            Check(small.Width == 1000 && small.Height == 600, "minimum dimensions protect the content");
            var resizedDisplay = policy.Resolve(new(2500, 1400, true), 1280, 700);
            Check(resizedDisplay.Width <= 1280 && resizedDisplay.Height <= 700 && resizedDisplay.Maximized, "smaller work area clamps bounds while retaining maximization");
            Check(policy.Resolve(new(double.NaN, 600), 1920, 1040).Width == 1180, "nonfinite settings fall back safely");
            Check(policy.Resolve(new(-100, 600), 1920, 1040).Width == 1180, "invalid dimensions are ignored");
            Check(policy.Resolve(null, 800, 400).Width == 1000, "undersized displays cannot silently remove the minimum");
            Check(WindowSizePolicy.Capture(normal, 2, 1920, 1080, 1260, 780) == normal with { Maximized = true }, "maximizing retains restore bounds");
            var maximized = normal with { Maximized = true };
            Check(WindowSizePolicy.Capture(maximized, 1, 0, 0, 0, 0) == maximized, "minimize does not overwrite normal size or prior maximization");
            Check(WindowSizePolicy.Capture(maximized, 0, 1188, 744, 0, 0) == new WindowSizePreference(1188, 744), "restoring and resizing replaces maximized state");

            var store = new WindowPreferencesStore(root, "main");
            Check(store.Read() is null, "new profiles have no saved size");
            Check(store.Save(normal), "size can be saved before runtime initialization");
            var database = new BlueLinkDatabase(root, root);
            await database.InitializeAsync(new IdentityStore(root));
            Check(new WindowPreferencesStore(root, "main").Read() == normal, "initial database migration preserves window preferences");
            var draft = await database.LoadSettingsAsync();
            await store.SaveAsync(maximized);
            await database.SaveSettingsAsync(draft with { Language = "en-US" });
            Check(new WindowPreferencesStore(root, "main").Read() == maximized, "saving an older settings draft cannot revert newer window bounds");
            Check(new WindowPreferencesStore(root, "preview").Save(new(940, 680)), "preview keeps its own dimensions");
            Check(new WindowPreferencesStore(root, "main").Read() == maximized, "secondary windows cannot replace the main size");
            var queued = Enumerable.Range(0, 20).Select(index => store.SaveAsync(new(1100 + index, 690))).ToArray();
            var final = new WindowSizePreference(1296, 812);
            Check(store.Save(final), "close-time flush succeeds");
            await Task.WhenAll(queued);
            Check(new WindowPreferencesStore(root, "main").Read() == final, "older asynchronous saves cannot overwrite close-time flush");
            using (var db = new NativeSqliteConnection(database.DatabasePath))
            {
                db.Execute("BEGIN EXCLUSIVE");
                Check(!store.Save(new(1320, 820)), "locked storage reports failure without crashing or resetting preferences");
                db.Execute("ROLLBACK");
            }
            Check(new WindowPreferencesStore(root, "main").Read() == final, "failed writes retain previous saved size");
            using (var db = new NativeSqliteConnection(database.DatabasePath))
                db.Execute("UPDATE app_setting SET value='{bad-json' WHERE key='window_size.main'");
            Check(new WindowPreferencesStore(root, "main").Read() is null, "corrupt saved data falls back instead of blocking startup");
            Check(store.Save(new(1200, 730)) && new WindowPreferencesStore(root, "main").Read() == new WindowSizePreference(1200, 730), "valid resizing repairs malformed preferences");
            Console.WriteLine($"BlueLink window size verification passed: {_checks} checks");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("BlueLinkWindowSize-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture root.");
            Directory.Delete(resolved, true);
        }
    }

    private void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
    }
}
