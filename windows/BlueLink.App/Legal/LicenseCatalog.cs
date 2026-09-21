namespace BlueLink.Legal;

public sealed record LicenseEntry(string Name, string License, string ResourceName)
{
    public string Text
    {
        get
        {
            using var stream = typeof(LicenseCatalog).Assembly.GetManifestResourceStream(
                $"BlueLink.Assets.Licenses.{ResourceName}") ?? throw new IOException("随包许可证不存在。");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

public static class LicenseCatalog
{
    public static IReadOnlyList<LicenseEntry> All { get; } = Array.AsReadOnly<LicenseEntry>(
    [
        new("SkiaSharp 4.152.1", "MIT License", "SkiaSharp.txt"),
        new("SkiaSharp 第三方组件", "Third-party notices", "SkiaSharp-ThirdPartyNotices.txt"),
        new("WPF-UI 4.3.0", "MIT License", "WPF-UI.txt"),
        new("WPF-UI.Abstractions 4.3.0", "MIT License", "WPF-UI-Abstractions.txt"),
        new("WPF-UI 第三方组件", "Third-party notices", "WPF-UI-ThirdPartyNotices.txt"),
        new("BouncyCastle.Cryptography 2.4.0", "Bouncy Castle License", "BouncyCastle.txt"),
        new("Noto Sans SC", "SIL Open Font License 1.1", "NotoSansSC.txt"),
        new("Phosphor Icons / BlueLink file icons", "MIT License", "PhosphorIcons.txt"),
    ]);
}
