namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal sealed record MenuFixture(string State, bool Outgoing, bool Image, string Name);
    internal static readonly IReadOnlyDictionary<string, MenuFixture> MenuFixtures = BuildMenuFixtures();

    private static IReadOnlyDictionary<string, MenuFixture> BuildMenuFixtures()
    {
        var result = new Dictionary<string, MenuFixture>(StringComparer.Ordinal);
        var states = new[]
        {
            ("waiting", "Offered", false, "等待接收"),
            ("sending", "Transferring", true, "发送中"),
            ("send-paused", "Paused", true, "发送暂停"),
            ("receiving", "Transferring", false, "接收中"),
            ("receive-paused", "Paused", false, "接收暂停"),
            ("send-failed", "Failed", true, "发送失败"),
            ("receive-failed", "Failed", false, "接收失败"),
            ("send-complete", "Completed", true, "发送完成"),
            ("receive-complete", "Completed", false, "接收完成"),
        };
        foreach (var (suffix, state, outgoing, label) in states)
        {
            foreach (var prefix in new[] { "files-menu-", "message-menu-" })
                result.Add(prefix + suffix, new(state, outgoing, false, "QA " + label + ".pdf"));
            if (suffix is "sending" or "receiving" or "send-complete" or "receive-complete")
                result.Add("image-menu-" + suffix, new(state, outgoing, true, "QA 图片" + label + ".png"));
        }
        return result;
    }
}
