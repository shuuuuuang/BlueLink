using System.Windows;
using System.Windows.Threading;
using BlueLink.Feedback;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal static readonly string[] FeedbackScenes =
    [
        "help-connection", "help-messages", "help-faq", "feedback-empty",
        "feedback-filled-off", "feedback-filled-on", "feedback-validation",
        "feedback-generating-off", "feedback-generating-on", "feedback-failure-off", "feedback-failure-on",
        "feedback-ready-off", "feedback-ready-on",
    ];

    private static async Task ApplyFeedbackFixtureAsync(MainWindow window, string directory, string scene)
    {
        window.OpenSettings();
        var page = window.ActiveSettingsPage!;
        page.ApplyFeedbackFixture(scene);
        if (!scene.StartsWith("feedback-ready-", StringComparison.Ordinal)) return;
        var package = await FeedbackPackageService.GenerateAsync(SettingsPage.AcceptanceFeedbackDraft(scene),
            Path.Combine(directory, $"BlueLink-feedback-QA-{Guid.NewGuid():N}.zip"),
            Path.Combine(directory, "diagnostics.log"));
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero) return;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => page.ShowFeedbackPackage(package)));
    }
}

public partial class SettingsPage
{
    internal static FeedbackDraft AcceptanceFeedbackDraft(string scene) => new("连接问题",
        "QA：连接后发送文件时进度停止，重试后仍未完成。期望显示失败原因，并保留已填写的反馈描述。",
        scene.EndsWith("-on", StringComparison.Ordinal));

    // Startup-only isolated visual fixtures; production generation always uses the actual service result.
    internal void ApplyFeedbackFixture(string scene)
    {
        SetFeedbackState(FeedbackPresentationState.Idle);
        ShowFeedback();
        if (scene.StartsWith("help-", StringComparison.Ordinal))
        {
            ShowHelpTopic(scene["help-".Length..]);
            return;
        }
        var draft = AcceptanceFeedbackDraft(scene);
        FeedbackDescription.Text = scene is "feedback-empty" or "feedback-validation" ? "" : draft.Description;
        FeedbackCategory.SelectedValue = draft.Category;
        FeedbackDiagnostics.IsChecked = draft.IncludeDiagnostics;
        SetFeedbackState(scene switch
        {
            "feedback-validation" => FeedbackPresentationState.Validation,
            "feedback-generating-off" or "feedback-generating-on" => FeedbackPresentationState.Generating,
            "feedback-failure-off" or "feedback-failure-on" => FeedbackPresentationState.Failure,
            _ => FeedbackPresentationState.Idle,
        }, scene.StartsWith("feedback-failure-", StringComparison.Ordinal) ? "QA：模拟本地写入失败，用于验收失败界面。" : null);
    }
}
