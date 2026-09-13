using System.Security.Cryptography;
using System.Text.Json;
using BlueLink.Security;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    // Isolated presentation fixture. No trust store, socket or transport is involved.
    private static void ShowSecurityFixture(MainWindow owner, string directory, string scene)
    {
        var digits = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var request = new TrustRequest("QA REDMI K80 Pro", digits[..3] + " " + digits[3..],
            TrustRequest.Fingerprint(DeviceIdentity.Generate().PublicKey),
            TrustRequest.Fingerprint(DeviceIdentity.Generate().PublicKey),
            trustedFingerprint: TrustRequest.Fingerprint(DeviceIdentity.Generate().PublicKey))
        { PeerPlatform = Domain.PeerPlatform.Android };
        if (scene == "security-waiting") request.Confirm();
        else if (scene != "security-confirm") request.Finish(scene switch
        {
            "security-rejected" => TrustStage.Rejected,
            "security-timeout" => TrustStage.TimedOut,
            "security-identity-changed" => TrustStage.IdentityChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(scene))
        });
        var window = new TrustConfirmationWindow(request) { Owner = owner };
        using (BlueLinkDialog.DimOwner(owner, "#48101828", 52)) window.ShowDialog();
        File.WriteAllText(Path.Combine(directory, "security-fixture-result.json"), JsonSerializer.Serialize(new
        {
            scene, stage = request.Stage.ToString(), request.SafetyCode,
            window.RetryRequested, window.ManageTrustRequested, transportStarted = false, trustSaved = false
        }));
        if (window.ManageTrustRequested) owner.OpenSettings(connections: true);
        if (window.RetryRequested) ShowSecurityFixture(owner, directory, "security-confirm");
    }
}
