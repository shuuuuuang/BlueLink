using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BlueLink.Feedback;

public sealed record FeedbackDraft(string Category, string Description, bool IncludeDiagnostics)
{
    public const int MaximumDescriptionLength = 1000;
    public static IReadOnlyList<string> Categories { get; } =
        Array.AsReadOnly(new[] { "连接问题", "消息与文件", "界面与操作", "其他问题" });

    public string? ValidationError => !Categories.Contains(Category)
        ? "请选择问题类型。"
        : string.IsNullOrWhiteSpace(Description)
            ? "请填写问题描述。"
            : Description.Length > MaximumDescriptionLength
                ? "问题描述不能超过 1000 字。"
                : null;
}

public sealed record FeedbackPackage(string Path, bool IncludesDiagnostics);

public static class FeedbackPackageService
{
    public static async Task<FeedbackPackage> GenerateAsync(FeedbackDraft draft, string targetPath,
        string diagnosticsPath, bool overwrite = false, CancellationToken token = default)
    {
        if (draft.ValidationError is { } error) throw new ArgumentException(error, nameof(draft));
        var target = Path.GetFullPath(targetPath);
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("反馈包必须保存为 .zip 文件。", nameof(targetPath));
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("保存目录无效。");
        var temporary = Path.Combine(directory, $".bluelink-feedback-{Guid.NewGuid():N}.tmp");

        return await Task.Run(async () =>
        {
            var created = false;
            try
            {
                token.ThrowIfCancellationRequested();
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 16384, FileOptions.Asynchronous))
                {
                    created = true;
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                    var metadata = new
                    {
                        schemaVersion = 1,
                        product = "BlueLink",
                        version = typeof(FeedbackPackageService).Assembly.GetName().Version?.ToString(3) ?? "未知",
                        createdAt = DateTimeOffset.Now,
                        platform = "Windows",
                        osVersion = Environment.OSVersion.Version.ToString(),
                        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        category = draft.Category,
                        description = draft.Description.Trim(),
                        includesDiagnostics = draft.IncludeDiagnostics,
                    };
                    await WriteEntryAsync(archive, "feedback.json",
                        JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), token);
                    if (draft.IncludeDiagnostics)
                    {
                        var diagnostics = await DiagnosticsSnapshot.ReadAsync(diagnosticsPath, token);
                        await WriteEntryAsync(archive, "diagnostics.log", diagnostics, token);
                    }
                    await WriteEntryAsync(archive, "README.txt",
                        "此反馈包由用户在本机生成，没有自动上传或发送。\n" +
                        "feedback.json 包含用户填写的问题描述、应用版本与系统版本。\n" +
                        (draft.IncludeDiagnostics
                            ? "diagnostics.log 只包含最近诊断事件的时间、操作和错误类型；不包含聊天内容、设备名称、文件路径、身份密钥或用户文件。\n"
                            : "未附加诊断日志。\n"), token);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite);
                return new FeedbackPackage(target, draft.IncludeDiagnostics);
            }
            finally
            {
                // Only this operation's exclusively created temporary file is eligible for cleanup.
                if (created && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }, token);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string value,
        CancellationToken token)
    {
        await using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await using var writer = new StreamWriter(entry, new UTF8Encoding(false));
        await writer.WriteAsync(value.AsMemory(), token);
    }
}
