using System.Text;
using System.Windows;
using BlueLink.Storage;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal static bool HasFileFixture(string? scene) => scene is
        "settings-trusted" or
        "files-stage-progress" or "files-device-transfer" or "files-device-transfer-paused" or "files-current" or "files-global" or "files-offline" or "files-bluetooth-off" or "message-history" or "search-results" or "search-empty" ||
        scene is not null && (MenuFixtures.ContainsKey(scene) || scene.StartsWith("toast-", StringComparison.Ordinal) || scene.StartsWith("security-", StringComparison.Ordinal));

    private static async Task SeedFileFixtureAsync(BlueLinkDatabase db, string directory, string scene, DateTimeOffset now)
    {
        var pdfPath = Path.Combine(directory, "产品需求文档.pdf");
        var textPath = Path.Combine(directory, "会议记录.txt");
        var imagePath = Path.Combine(directory, "Screenshot-QA.png");
        await File.WriteAllBytesAsync(pdfPath, CreatePdfFixture());
        await File.WriteAllTextAsync(textPath, "BlueLink QA：仅用于文件列表、搜索和详情的隔离桌面验收。", Encoding.UTF8);
        using (var source = Application.GetResourceStream(new Uri("/BlueLink;component/Assets/Figma/qa-prototype-image.png", UriKind.Relative)).Stream)
        using (var target = File.Create(imagePath)) await source.CopyToAsync(target);

        var files = new (string Name, string Mime, string State, bool Outgoing, string? Path, long Size, double Progress)[]
        {
            ("产品需求文档.pdf", "application/pdf", "Transferring", false, null, 2_600_468, .68),
            ("产品演示视频.mp4", "video/mp4", "Transferring", false, null, 19_503_514, .42),
            ("产品需求文档.pdf", "application/pdf", "Completed", true, pdfPath, new FileInfo(pdfPath).Length, 1),
            ("会议记录.txt", "text/plain", "Completed", true, textPath, new FileInfo(textPath).Length, 1),
            ("Screenshot-QA.png", "image/png", "Completed", false, imagePath, new FileInfo(imagePath).Length, 1),
            ("产品演示视频.mp4", "video/mp4", "Failed", true, null, 19_503_514, 0),
        };
        var menuScene = MenuFixtures.TryGetValue(scene, out var menuFixture);
        if (menuFixture is { } fixture)
        {
            var path = fixture.Image ? imagePath : pdfPath;
            files = [(fixture.Name, fixture.Image ? "image/png" : "application/pdf", fixture.State, fixture.Outgoing,
                fixture.Outgoing || fixture.State == "Completed" ? path : null, new FileInfo(path).Length,
                fixture.State == "Completed" ? 1 : fixture.State is "Transferring" or "Paused" ? .68 : 0)];
        }
        for (var index = 0; index < files.Length; index++)
        {
            var item = files[index];
            var peer = (scene == "files-global" ? index % 3 + 1 : 1).ToString("X32");
            var messageId = Guid.NewGuid().ToString("N");
            var transferId = Guid.NewGuid().ToString("N");
            var time = now.AddMinutes(-30 - index).ToUnixTimeMilliseconds();
            await db.UpsertMessageAsync(new(messageId, $"peer:{peer}", peer,
                item.Outgoing ? StoredMessageDirection.Outgoing : StoredMessageDirection.Incoming,
                item.Mime == "image/png" ? StoredMessageType.Image : StoredMessageType.File, "",
                item.Outgoing ? "Delivered" : "Received", time, index));
            await db.UpsertAttachmentAsync(new(Guid.NewGuid().ToString("N"), messageId, transferId, item.Name,
                item.Mime, item.Size, null, item.Path, item.Mime == "image/png" ? imagePath : null, item.State));
            await db.UpsertTransferAsync(new(transferId, peer, messageId, item.Outgoing ? "Outgoing" : "Incoming",
                item.State, item.Name, item.Mime, item.Size, (long)(item.Size * item.Progress), item.Path, null, null,
                item.State == "Failed" ? "QA_TIMEOUT" : null, item.State == "Failed" ? "QA 测试：对端连接中断" : null, time, time));
        }
        // These two records make a current-device/global-device switch observable.
        if (scene != "files-global" && !menuScene)
            for (var peerIndex = 2; peerIndex <= 3; peerIndex++)
            {
                var peer = peerIndex.ToString("X32");
                var activeDevice = peerIndex == 2 && scene.StartsWith("files-device-transfer", StringComparison.Ordinal);
                var deviceSize = activeDevice ? 2_600_468 : new FileInfo(textPath).Length;
                var deviceState = activeDevice ? (scene.EndsWith("paused", StringComparison.Ordinal) ? "Paused" : "Transferring") : "Completed";
                await db.UpsertTransferAsync(new(Guid.NewGuid().ToString("N"), peer, null, "Incoming", deviceState,
                    $"QA 其他设备 {peerIndex}.txt", "text/plain", deviceSize, activeDevice ? (long)(deviceSize * .68) : deviceSize,
                    activeDevice ? null : textPath, null, null, null, null, now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds()));
            }
        if (scene != "files-global")
        {
            const string peer = "00000000000000000000000000000001";
            var messages = new[]
            {
                (false, "你好，我已经连接上 BlueLink 了。", -20),
                (true, "收到，我把需求文档发给你。", -19),
                (false, "文件收到了，谢谢！", -18),
                (true, "不客气，有问题随时发消息。", -1),
            };
            if (menuScene) messages = [(false, "你好，我已经连接上 BlueLink 了。", -31), (true, "这是隔离的菜单验收记录。", -1)];
            foreach (var (outgoing, text, minutes) in messages)
                await db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), $"peer:{peer}", peer,
                    outgoing ? StoredMessageDirection.Outgoing : StoredMessageDirection.Incoming, StoredMessageType.Text,
                    text, outgoing ? "Delivered" : "Received", now.AddMinutes(minutes).ToUnixTimeMilliseconds(), 20 + minutes));
        }
    }

    private static byte[] CreatePdfFixture()
    {
        const string content = "BT /F1 18 Tf 40 760 Td (BlueLink desktop QA fixture) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var start = output.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n");
        return output.ToArray();
    }
}
