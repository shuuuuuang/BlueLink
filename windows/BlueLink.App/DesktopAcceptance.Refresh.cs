using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BlueLink.Domain;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    private static void EnableRefreshFixture(MainViewModel model, string directory)
    {
        var starts = 0;
        model.ScanForAcceptance = async () =>
        {
            var run = ++starts;
            void Record(string phase) => File.AppendAllText(Path.Combine(directory, "refresh-events.jsonl"),
                JsonSerializer.Serialize(new { run, phase, time = DateTimeOffset.UtcNow }) + Environment.NewLine);
            Record("started");
            await Task.Delay(TimeSpan.FromSeconds(12));
            Record("completed");
            return (IReadOnlyList<NearbyDevice>)new[] { new NearbyDevice("qa-refresh", "QA refresh result", "00:00:00:00:00:05", PeerPlatform.Android,
                LastSeen: DateTimeOffset.UtcNow, CanInitiate: true) };
        };
    }
}
