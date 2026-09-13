using BlueLink.Notifications;

internal static class NotificationVerification
{
    public static void Run()
    {
        var center = new NotificationCenter(); var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var first = Guid.NewGuid();
        center.Receive("a", "QA Phone", first, "第一行\n第二行", true);
        Check(center.UnreadCount == 0, "visible conversation does not notify");
        center.Receive("a", "QA Phone", first, "duplicate", false);
        Check(center.UnreadCount == 0, "duplicate of a visible message does not create a background notification");
        var second = Guid.NewGuid(); center.Receive("a", "QA Phone", second, "hello", false);
        center.Receive("a", "QA Phone", second, "duplicate", false);
        Check(center.UnreadCount == 1 && center.Conversations.Count == 1, "duplicate background event counts once");
        center.Receive("a", "QA Phone", Guid.NewGuid(), "new\nline", false);
        center.Receive("b", "QA Laptop", Guid.NewGuid(), "file", false);
        Check(center.UnreadCount == 3 && center.Conversations.Count == 2, "notifications aggregate by conversation");
        Check(center.Conversations.Single(p => p.PeerId == "a").Preview == "new line", "multiline previews stay on one line");
        center.UpdatePreview("b", "文件接收完成");
        Check(center.UnreadCount == 3 && center.Conversations.Single(p => p.PeerId == "b").Preview == "文件接收完成", "file completion updates the existing notification without double counting");
        center.Read("a"); Check(center.UnreadCount == 1 && center.Conversations.Single().PeerId == "b", "reading one conversation preserves other unread messages");
        center.Read("b"); Check(center.UnreadCount == 0, "reading all conversations clears the badge");
        center.Restore("a", "QA Phone", 7, DateTimeOffset.UtcNow);
        center.Restore("a", "QA Phone", 7, DateTimeOffset.UtcNow);
        Check(center.UnreadCount == 7, "reloading persisted unread totals is idempotent");
        center.Receive("a", "QA Phone", Guid.NewGuid(), new string('长', 300), false);
        Check(center.Conversations.Single().Preview.Length == 141, "previews have a bounded length");
        center.Read("A"); Check(center.UnreadCount == 0, "peer identity lookup ignores case");
        center.Receive("b", "QA Laptop", Guid.NewGuid(), "clear history", false);
        center.Clear(); Check(center.UnreadCount == 0 && center.Conversations.Count == 0, "clearing history removes obsolete notification entries");
        Console.WriteLine($"BlueLink notification verification passed: {checks} checks");
    }
}
