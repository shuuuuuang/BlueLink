using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Security;
using BlueLink.Storage;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyIdentityAssociationDesign(string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "zh-CN", "en-US" })
        foreach (var height in new[] { 576, 420 })
        {
            AppearanceService.Apply(BlueLinkSettings.Defaults(output) with { Theme = theme, Language = language });
            var oldKey = DeviceIdentity.Generate().PublicKey;
            var request = new TrustRequest("QA 电脑", "729 348", "D774:3514:983B:8204:5419:1618", "A92C:0E6F:7B44:19D2:63A8:C501",
                trustedFingerprint: TrustRequest.Fingerprint(oldKey), identityCandidate: new("old-peer", "原电脑备注", oldKey, "bt:AABBCCDDEEFF"));
            var window = new TrustConfirmationWindow(request);
            var root = DetachForRendering(window);
            DrainDispatcher();
            Capture(root, output, $"identity-{theme}-{language}-{height}", 620, height);
            var primary = (Button)window.FindName("TrustPrimaryButton");
            var secondary = (Button)window.FindName("TrustCloseButton");
            var scroll = (ScrollViewer)window.FindName("TrustContentScroll");
            Check(request.FirstFingerprint == TrustRequest.Fingerprint(oldKey) && request.PrimaryText == primary.Content.ToString(), "old fingerprint and explicit association action are rendered");
            var footer = primary.TranslatePoint(new Point(), root);
            Check(footer.Y >= scroll.TranslatePoint(new Point(0, scroll.ActualHeight), root).Y &&
                footer.Y + primary.ActualHeight <= height, "scrolling content cannot overlap fixed confirmation controls");
            scroll.ScrollToEnd();
            Layout(root, 620, height);
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "all identity explanation remains reachable in compact windows");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(height == 576 ? primary : secondary);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            DrainDispatcher();
            Check(request.Stage == (height == 576 ? TrustStage.Waiting : TrustStage.Canceled), "UI Automation confirms or cancels the real request");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "identity acceptance never opens a desktop window");
            window.Close();
        }
    }
}
