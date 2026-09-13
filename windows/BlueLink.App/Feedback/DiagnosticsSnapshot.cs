using System.Text;
using System.Text.RegularExpressions;

namespace BlueLink.Feedback;

public static class DiagnosticsSnapshot
{
    public const int MaximumSourceBytes = 1024 * 1024;
    private static readonly Regex Header = new(
        @"^\[(?<time>[^\]]{1,40})\] (?<component>Session|Transfer) \| (?<message>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Error = new(
        @" \| (?<type>[A-Za-z][A-Za-z0-9.]{0,100}Exception) \(0x(?<code>[0-9A-Fa-f]{8})\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly string[] Operations =
    [
        "会话开始", "安全会话已建立", "对端在", "握手失败", "通信失败", "安全握手失败",
        "已创建不可变发送快照", "无法生成图片预览", "图片预览发送失败", "清理发送快照失败",
        "清理重试快照失败", "发送 Offer", "从持久化断点继续", "对端已校验并落盘",
        "文件发送失败", "对端暂停文件传输", "对端继续文件传输", "已收到暂不支持的控制指令",
        "对端取消文件传输", "暂缓超限 Offer", "拒绝 Offer", "接受 Offer",
        "关闭失败的接收文件失败", "接收数据块失败", "数据块接收完成", "整文件哈希校验通过",
        "文件校验并原子落盘完成", "关闭校验失败的接收文件失败", "文件完成校验失败",
        "关闭被取消的接收文件失败", "收到对端失败通知", "发送失败通知失败", "会话结束时关闭接收文件失败",
    ];

    public static async Task<string> ReadAsync(string path, CancellationToken token = default)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return "BlueLink 尚未生成诊断日志。\n"; }
        catch (DirectoryNotFoundException) { return "BlueLink 尚未生成诊断日志。\n"; }
        await using (stream)
        {
            var length = stream.Length;
            var count = (int)Math.Min(length, MaximumSourceBytes);
            var tail = new byte[count];
            stream.Seek(Math.Max(0, length - count), SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var current = await stream.ReadAsync(tail.AsMemory(read, count - read), token);
                if (current == 0) break;
                read += current;
            }
            var value = Encoding.UTF8.GetString(tail, 0, read);
            if (length > count)
            {
                var newline = value.IndexOf('\n');
                value = newline < 0 ? "" : value[(newline + 1)..];
            }
            return Project(value, length > count, token);
        }
    }

    private static string Project(string source, bool truncated, CancellationToken token)
    {
        var result = new StringBuilder(truncated
            ? "仅导出日志末尾 1 MiB 范围内的完整事件。\n"
            : "诊断事件摘要。\n");
        using var lines = new StringReader(source);
        var count = 0;
        while (lines.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            var header = Header.Match(line);
            if (!header.Success || !DateTimeOffset.TryParse(header.Groups["time"].Value, out var time)) continue;
            var message = header.Groups["message"].Value;
            var operation = Operations.FirstOrDefault(value => message.StartsWith(value, StringComparison.Ordinal))
                ?? "诊断事件";
            if (operation == "对端在") operation = "对端关闭连接";
            result.Append('[').Append(time.ToString("O")).Append("] ")
                .Append(header.Groups["component"].Value).Append(" | ").Append(operation);
            var error = Error.Match(message);
            if (error.Success)
                result.Append(" | ").Append(error.Groups["type"].Value)
                    .Append(" (0x").Append(error.Groups["code"].Value).Append(')');
            result.AppendLine();
            count++;
        }
        if (count == 0) result.AppendLine("尚无可导出的诊断事件。");
        return result.ToString();
    }
}
