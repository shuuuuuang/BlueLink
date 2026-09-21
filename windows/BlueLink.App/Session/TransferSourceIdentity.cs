using BlueLink.Domain;

namespace BlueLink.Session;

internal static class TransferSourceIdentity
{
    public static void Validate(TransferItem previous, long size, ReadOnlySpan<byte> sha256)
    {
        if (previous.SourceSha256 is not { Length: 64 } expected || !expected.All(Uri.IsHexDigit))
            throw new InvalidOperationException(Localization.Strings.Get("旧任务缺少文件指纹，请重新选择文件发送"));
        if (size != previous.TotalBytes || !expected.Equals(Convert.ToHexString(sha256), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localization.Strings.Get("文件内容已变化，请重新选择文件发送"));
    }
}
